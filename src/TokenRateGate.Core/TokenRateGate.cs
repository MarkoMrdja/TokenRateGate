using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Abstractions;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.TokenEstimation;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.Core;

public class TokenRateGate : ITokenRateGate, IDisposable
{
    // ============================================================================
    // CONFIGURATION AND DEPENDENCIES
    // ============================================================================

    private readonly TokenRateGateOptions _options;
    private readonly ILogger<TokenRateGate> _logger;
    private readonly TokenRateGateMetrics? _metrics;
    private readonly ITokenEstimator _tokenEstimator;

    private readonly int _safetyBuffer; // Calculated from SafetyBufferPercentage

    private readonly object _lock = new();

    // ============================================================================
    // TOKEN TRACKING
    // ============================================================================

    // Completed requests timeline
    private readonly Queue<TokenUsageEntry> _usageTimeline = new();
    private long _currentActualUsage;

    // Executing requests
    private readonly Dictionary<Guid, PendingReservations> _activeReservations = new();

    // Waiting requests
    private readonly LinkedList<WaitingRequest> _waitingRequests = new();

    // Request timeline for rate limiting
    private readonly Queue<DateTime> _requestTimeline = new();

    private readonly SemaphoreSlim _concurrencyLimiter;

    private readonly Timer _safetyTimer;
    private volatile bool _disposed;

    private DateTime _lastCleanup = DateTime.MinValue;

    // Flag to avoid wasteful queue processing when capacity is known to be insufficient
    // Reset when tokens are freed during cleanup
    private bool _hasInsufficientCapacity = false;

    // ============================================================================
    // CONSTANTS - Internal Operation Timing
    // ============================================================================

    /// <summary>
    /// NOTE: Cleanup throttling has been removed for optimal high-throughput performance.
    /// Cleanup is now called on every reservation attempt, which is safe because:
    /// 1. Cleanup is O(k) where k = number of expired items (typically 1-20)
    /// 2. The lock is already held during reservation attempts
    /// 3. DateTime comparisons and dequeuing are extremely fast (microseconds)
    /// 4. This ensures zero-latency token freeing for maximum throughput
    /// The _lastCleanup field is kept for potential future metrics/debugging.
    /// </summary>
    private readonly TimeSpan _internalOperationInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum request history window in seconds. Request timeline is kept for at least
    /// this duration or 2x the token window, whichever is larger.
    /// Rationale: 120 seconds (2 minutes) ensures we have sufficient history for
    /// request-per-minute calculations even with small token windows.
    /// </summary>
    private const int MinRequestHistoryWindowSeconds = 120;

    /// <summary>
    /// Multiplier for request history window relative to token window.
    /// Rationale: 2x ensures we capture sufficient request history for rate calculations.
    /// </summary>
    private const int RequestHistoryWindowMultiplier = 2;

    /// <summary>
    /// Minimum timeout before considering a reservation stale (in seconds).
    /// Rationale: 600 seconds (10 minutes) is reasonable minimum - shorter would risk
    /// cleaning up legitimately slow requests, longer would waste memory.
    /// </summary>
    private const int MinStaleReservationTimeoutSeconds = 600;

    /// <summary>
    /// Maximum timeout before considering a reservation stale (in seconds).
    /// Rationale: 86400 seconds (24 hours) is absolute maximum - any reservation
    /// older than this is definitely stale and should be cleaned up.
    /// </summary>
    private const int MaxStaleReservationTimeoutSeconds = 86400;

    /// <summary>
    /// Multiplier for stale reservation timeout relative to token window.
    /// Rationale: 10x the token window is generous buffer for request completion
    /// while ensuring stale reservations don't accumulate indefinitely.
    /// </summary>
    private const int StaleReservationTimeoutMultiplier = 10;

    // ============================================================================
    // CONSTRUCTORS
    // ============================================================================

    public TokenRateGate(IOptions<TokenRateGateOptions> options, ILogger<TokenRateGate> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateOptions();

        // Initialize token estimator (use provided or default to CharacterBasedTokenEstimator)
        _tokenEstimator = _options.TokenEstimator ?? new CharacterBasedTokenEstimator();

        // Calculate safety buffer from percentage
        _safetyBuffer = (int)(_options.TokenLimit * _options.SafetyBufferPercentage);

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);

        _safetyTimer = new Timer(SafetyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

        // Initialize metrics if OpenTelemetry is enabled
        _metrics = new TokenRateGateMetrics(this);
    }

    public TokenRateGate(TokenRateGateOptions options, ILogger<TokenRateGate> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateOptions();

        // Initialize token estimator (use provided or default to CharacterBasedTokenEstimator)
        _tokenEstimator = _options.TokenEstimator ?? new CharacterBasedTokenEstimator();

        // Calculate safety buffer from percentage
        _safetyBuffer = (int)(_options.TokenLimit * _options.SafetyBufferPercentage);

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);

        _safetyTimer = new Timer(SafetyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

        // Initialize metrics if OpenTelemetry is enabled
        _metrics = new TokenRateGateMetrics(this);
    }

    // ============================================================================
    // MAIN IMPLEMENTATION
    // ============================================================================

    public async Task<TokenReservation> ReserveTokensAsync(int inputTokens, int estimatedOutputTokens = 0, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGate));

        if (inputTokens <= 0)
            throw new ArgumentException("Input tokens must be positive", nameof(inputTokens));

        if (estimatedOutputTokens < 0)
            throw new ArgumentException("Estimated output tokens cannot be negative", nameof(estimatedOutputTokens));

        int totalEstimatedTokens = CalculateEstimatedTotalTokens(inputTokens, estimatedOutputTokens);

        // Record reservation request metric
        _metrics?.RecordReservationRequested();

        // Validate that the request is possible given the token limit and safety buffer
        int effectiveLimit = _options.TokenLimit - _safetyBuffer;
        if (totalEstimatedTokens > effectiveLimit)
        {
            throw new ArgumentException(
                $"Requested tokens ({totalEstimatedTokens:N0}) exceeds effective capacity ({effectiveLimit:N0}). " +
                $"Token limit: {_options.TokenLimit:N0}, Safety buffer: {_safetyBuffer:N0} ({_options.SafetyBufferPercentage:P0}). " +
                $"This request can never be fulfilled.", nameof(inputTokens));
        }

        // Start timing from the very beginning of the reservation attempt
        var reservationStartTime = DateTime.UtcNow;

        // Create timeout token if MaxWaitTime is configured
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? combinedCts = null;
        CancellationToken effectiveCancellationToken = cancellationToken;

        try
        {
            if (_options.MaxWaitTime.HasValue)
            {
                timeoutCts = new CancellationTokenSource(_options.MaxWaitTime.Value);
                combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                effectiveCancellationToken = combinedCts.Token;
            }

            await _concurrencyLimiter.WaitAsync(effectiveCancellationToken);

            bool semaphoreAcquired = true;
            try
            {
                // Try immediate reservation, the fast path
                if (TryReserveImmediately(totalEstimatedTokens, out var immediateId))
                {
                    _logger.LogDebug("Immediate reservation: {TotalTokens} tokens (input: {InputTokens}, estimated output: {EstimatedOutput} with ID {ReservationID}",
                        totalEstimatedTokens, inputTokens, totalEstimatedTokens - inputTokens, immediateId);

                    // Record successful immediate grant
                    _metrics?.RecordReservationGranted();

                    // UpdateSafetyTimerState is already called inside TryReserveImmediately under lock

                    // Keep semaphore acquired - it will be released when reservation is disposed
                    // This ensures MaxConcurrentRequests limits active reservations, not just admission
                    semaphoreAcquired = false; // Ownership transferred to TokenReservation

                    return new TokenReservation(immediateId, totalEstimatedTokens, inputTokens, ReleaseReservationAsync, _concurrencyLimiter, RecordActualUsageCallback);
                }

                var waitingRequest = new WaitingRequest(totalEstimatedTokens, cancellationToken);
                LinkedListNode<WaitingRequest> node;
                CancellationTokenRegistration cancellationRegistration = default;

                lock (_lock)
                {
                    if (TryReserveImmediatelyInternal(totalEstimatedTokens, out var doubleCheckId))
                    {
                        _logger.LogDebug("Double-check reservation: {TotalTokens} tokens with ID {ReservationId}",
                            totalEstimatedTokens, doubleCheckId);

                        // Record successful grant after double-check
                        _metrics?.RecordReservationGranted();

                        UpdateSafetyTimerState();

                        // Keep semaphore acquired - it will be released when reservation is disposed
                        semaphoreAcquired = false; // Ownership transferred to TokenReservation

                        return new TokenReservation(doubleCheckId, totalEstimatedTokens, inputTokens, ReleaseReservationAsync, _concurrencyLimiter, RecordActualUsageCallback);
                    }

                    node = _waitingRequests.AddLast(waitingRequest);
                    waitingRequest.Node = node;

                    long currentUsage = GetCurrentUsageInternal();
                    long reservedTokens = GetReservedTokensInternal();

                    _logger.LogInformation("Added to waiting queue: {TotalTokens} tokens (input: {InputTokens}, estimated output: {EstimatedOutput}). " +
                       "Queue length: {QueueLength}, Current usage: {CurrentUsage}/{TokenLimit} ({UsagePercent:F1}%), " +
                       "Reserved: {ReservedTokens}, Available: {AvailableTokens}",
                        totalEstimatedTokens, inputTokens, totalEstimatedTokens - inputTokens, _waitingRequests.Count,
                        currentUsage, _options.TokenLimit, (double)currentUsage / _options.TokenLimit * 100,
                        reservedTokens, Math.Max(0, _options.TokenLimit - _safetyBuffer - currentUsage));

                    // Register cancellation callback INSIDE the lock to avoid race condition
                    // The callback must check if the request is still in the queue before cancelling
                    cancellationRegistration = effectiveCancellationToken.Register(() =>
                    {
                        lock (_lock)
                        {
                            // Only cancel if still in queue (node not null and not already granted)
                            if (waitingRequest.Node != null && _waitingRequests.Contains(waitingRequest.Node.Value))
                            {
                                _waitingRequests.Remove(waitingRequest.Node);
                                waitingRequest.Node = null;

                                // Set cancelled AFTER removing from queue to prevent race
                                waitingRequest.TaskCompletionSource.TrySetCanceled(effectiveCancellationToken);

                                _logger.LogDebug("Cancelled waiting request: {TotalTokens} tokens", totalEstimatedTokens);

                                // Record cancellation metric
                                _metrics?.RecordReservationCancelled();

                                UpdateSafetyTimerState();
                            }
                        }
                    });

                    UpdateSafetyTimerState();
                }

                try
                {
                    waitingRequest.CancellationToken = effectiveCancellationToken;

                    var reservationId = await waitingRequest.TaskCompletionSource.Task;

                    var totalWaitTime = DateTime.UtcNow - reservationStartTime;
                    _logger.LogDebug(
                        "Granted queued reservation: {TotalTokens} tokens with ID {ReservationId} after waiting {WaitTime:mm\\:ss}",
                        totalEstimatedTokens, reservationId, totalWaitTime);

                    // Record successful grant after queue wait and wait time
                    _metrics?.RecordReservationGranted();
                    _metrics?.RecordWaitTime(totalWaitTime);

                    // Keep semaphore acquired - it will be released when reservation is disposed
                    semaphoreAcquired = false; // Ownership transferred to TokenReservation

                    return new TokenReservation(reservationId, totalEstimatedTokens, inputTokens, ReleaseReservationAsync, _concurrencyLimiter, RecordActualUsageCallback);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested &&
                                                            !cancellationToken.IsCancellationRequested)
                {
                    var elapsedTime = DateTime.UtcNow - reservationStartTime;

                    // Record timeout metric
                    _metrics?.RecordReservationTimeout();

                    var maxWaitTimeStr = _options.MaxWaitTime.HasValue
                        ? $"{_options.MaxWaitTime.Value.TotalMinutes:F1} minutes"
                        : "unlimited";

                    throw new TimeoutException(
                        $"Unable to acquire token capacity after waiting {elapsedTime.TotalMinutes:F1} minutes. " +
                        $"Requested: {totalEstimatedTokens:N0} tokens. Maximum wait time: {maxWaitTimeStr}.");
                }
                finally
                {
                    cancellationRegistration.Dispose();
                }
            }
            catch
            {
                if (semaphoreAcquired)
                    _concurrencyLimiter.Release();
                throw;
            }
        }
        finally
        {
            // Clean up timeout resources
            timeoutCts?.Dispose();
            combinedCts?.Dispose();
        }
    }

    private int CalculateEstimatedTotalTokens(int inputTokens, int estimatedOutputTokens)
    {
        long total;

        // If user explicitly provided estimatedOutputTokens (including 0), use it directly
        // Estimation strategies are only used when estimatedOutputTokens is provided as the default (0)
        // and the caller intends to use estimation. For explicit 0 (no output tokens), we honor that.
        // To distinguish: if someone wants estimation, they should not pass estimatedOutputTokens parameter
        // Since we can't distinguish "not passed" from "passed 0" with current signature,
        // we treat any explicit value >= 0 as intentional, only using strategy for very specific cases
        //
        // UPDATE: Simpler approach - always use explicit estimatedOutputTokens if >= 0
        // The estimation strategy documentation should clarify it's for when you don't know output size
        if (estimatedOutputTokens >= 0)
        {
            total = (long)inputTokens + estimatedOutputTokens;
        }
        else if (_options.OutputEstimationStrategy == OutputEstimationStrategy.FixedMultiplier)
        {
            long estimatedOutput = (long)Math.Ceiling(inputTokens * _options.OutputMultiplier);
            total = inputTokens + estimatedOutput;
        }
        else if (_options.OutputEstimationStrategy == OutputEstimationStrategy.FixedAmount)
        {
            total = (long)inputTokens + _options.DefaultOutputTokens;
        }
        else
        {
            total = (long)inputTokens * 2;
        }

        // Check for overflow and ensure result fits in int
        if (total > int.MaxValue)
        {
            throw new ArgumentException(
                $"Calculated total tokens ({total:N0}) exceeds maximum allowed value ({int.MaxValue:N0}). " +
                $"Input tokens: {inputTokens:N0}, Estimated output: {(total - inputTokens):N0}");
        }

        return (int)total;
    }

    private bool TryReserveImmediately(int requiredTokens, out Guid immediateId)
    {
        lock (_lock)
        {
            bool success = TryReserveImmediatelyInternal(requiredTokens, out immediateId);
            if (success)
            {
                UpdateSafetyTimerState();
            }
            return success;
        }
    }

    private bool TryReserveImmediatelyInternal(int requiredTokens, out Guid immediateId)
    {
        CleanupExpiredRecords();

        if (HasCapacityInternal(requiredTokens))
        {
            immediateId =  Guid.NewGuid();
            var reservation = new PendingReservations(immediateId, requiredTokens, DateTime.UtcNow);
            _activeReservations[immediateId] = reservation;

            _requestTimeline.Enqueue((DateTime.UtcNow));

            return true;
        }

        immediateId = Guid.Empty;
        return false;
    }

    // Must be called inside the lock
    // Note: This method intentionally holds the lock while calling TryProcessingWaitingRequests()
    // to ensure atomicity between freeing tokens and granting new reservations. While this extends
    // lock hold time, it prevents race conditions and is acceptable since:
    // 1. Cleanup is fast - O(k) where k = number of expired items (typically 1-20)
    // 2. Processing waiting requests is typically fast (immediate capacity checks)
    // 3. Alternative (releasing lock before processing) would introduce race conditions
    private void CleanupExpiredRecords(bool forceCleanup = false)
    {
        var now =  DateTime.UtcNow;

        // Throttling removed for high-throughput performance
        // Cleanup is fast (O(k) where k = expired items) and lock is already held

        bool tokensFreed = CleanupTokenTimeline(now);
        CleanupRequestTimeline(now);
        CleanupStaleReservations(now);

        _lastCleanup = now;

        if (tokensFreed && _waitingRequests.Count > 0)
            TryProcessingWaitingRequests();
    }

    private bool CleanupTokenTimeline(DateTime now)
    {
        var cutoff = now.AddSeconds(-_options.WindowSeconds);
        int removedTokens = 0;
        int removedEntries = 0;

        while (_usageTimeline.Count > 0 && _usageTimeline.Peek().Timestamp < cutoff)
        {
            var expired =  _usageTimeline.Dequeue();
            removedTokens += expired.ActualTokens;
            removedEntries++;
        }
        
        if (removedTokens > 0)
        {
            _currentActualUsage -= removedTokens;

            // Reset insufficient capacity flag since tokens were freed
            _hasInsufficientCapacity = false;

            _logger.LogDebug("Cleaned {RemovedTokens} expired tokens ({RemovedEntries} entries). Current usage: {CurrentUsage}",
                             removedTokens, removedEntries, _currentActualUsage);

            return true;
        }

        return false;
    }

    private void CleanupRequestTimeline(DateTime now)
    {
        var requestHistoryWindow = TimeSpan.FromSeconds(
            Math.Max(MinRequestHistoryWindowSeconds, _options.WindowSeconds * RequestHistoryWindowMultiplier));
        var cutoff = now.Subtract(requestHistoryWindow);

        while (_requestTimeline.Count > 0 && _requestTimeline.Peek() < cutoff)
        {
            _requestTimeline.Dequeue();
        }
    }

    private void CleanupStaleReservations(DateTime now)
    {
        var staleTimeout = TimeSpan.FromSeconds(
            Math.Max(MinStaleReservationTimeoutSeconds,
                Math.Min(MaxStaleReservationTimeoutSeconds,
                    _options.WindowSeconds * StaleReservationTimeoutMultiplier)));
        var staleReservationCutoff = now.Subtract(staleTimeout);
        
        var staleIds = _activeReservations
            .Where(kv => kv.Value.Timestamp < staleReservationCutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            _activeReservations.Remove(id);
            _logger.LogWarning("Removed stale reservation {ReservationId}", id);
        }
    }

    public void TryProcessingWaitingRequests()
    {
        List<WaitingRequest>? grantedRequests;

        lock (_lock)
        {
            if (_waitingRequests.Count == 0)
            {
                UpdateSafetyTimerState();
                return;
            }

            // Force cleanup to ensure tokens are freed immediately for waiting requests
            CleanupExpiredRecords(forceCleanup: true);

            var currentNode = _waitingRequests.First;
            grantedRequests = new List<WaitingRequest>();

            while (currentNode != null)
            {
                var request = currentNode.Value;
                var nextNode = currentNode.Next;

                // Check cancellation - if cancelled, remove and skip
                if (request.CancellationToken.IsCancellationRequested)
                {
                    _waitingRequests.Remove(currentNode);
                    request.Node = null;
                    // Don't set result here - the cancellation callback already did it

                    _logger.LogDebug("Removed cancelled request: {RequiredTokens} tokens", request.RequiredTokens);

                    currentNode = nextNode;
                    continue;
                }

                // Try to grant this request
                if (TryReserveImmediatelyInternal(request.RequiredTokens, out Guid reservationId))
                {
                    _waitingRequests.Remove(currentNode);
                    request.Node = null;
                    request.ReservationId = reservationId;
                    grantedRequests.Add(request);

                    _logger.LogDebug("Granted waiting request: {RequiredTokens} tokens with ID {ReservationID} (queue: {Remaining})",
                        request.RequiredTokens, reservationId, _waitingRequests.Count);
                }
                else
                {
                    // No more capacity available, stop processing
                    _hasInsufficientCapacity = true;

                    _logger.LogDebug("Insufficient capacity for {RequiredTokens} tokens, stopping queue processing " +
                                     "(current usage: {CurrentUsage}/{TokenLimit})",
                        request.RequiredTokens, GetCurrentUsageInternal(), _options.TokenLimit);

                    break;
                }

                currentNode = nextNode;
            }

            UpdateSafetyTimerState();
        }

        if (grantedRequests != null)
        {
            foreach (var request in grantedRequests)
                request.TaskCompletionSource.TrySetResult(request.ReservationId);
        }
    }
    
    // Must be called within the lock
    private void UpdateSafetyTimerState()
    {
        bool hasWaitingRequests =  _waitingRequests.Count > 0;
        bool hasActiveReservations = _activeReservations.Count > 0;

        // Only run timer when we have waiting requests but no active work - deadlock scenario
        bool shouldRunTimer = hasWaitingRequests && !hasActiveReservations;

        if (shouldRunTimer)
        {
            // Use a shorter interval (100ms) for the safety timer to ensure it triggers quickly
            // The safety timer uses forceCleanup=true so cleanup throttling doesn't apply
            _safetyTimer.Change(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
            _logger.LogDebug("Safety timer STARTED (waiting: {WaitingCount}, active: {ActiveCount})",
                            _waitingRequests.Count, _activeReservations.Count);
        }
        else
        {
            _safetyTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (hasWaitingRequests || hasActiveReservations)
                _logger.LogDebug("Safety timer STOPPED (waiting: {WaitingCount}, active: {ActiveCount})",
                                _waitingRequests.Count, _activeReservations.Count);
        }
    }

    private void SafetyTimerCallback(object? state)
    {
        if (_disposed) return;

        try
        {
            lock (_lock)
            {
                if (_waitingRequests.Count > 0 && _activeReservations.Count == 0)
                {
                    // Always cleanup to free expired tokens (throttling removed for performance)
                    CleanupExpiredRecords(forceCleanup: true);

                    // Only log if we're going to try processing (avoid log spam)
                    if (!_hasInsufficientCapacity)
                    {
                        _logger.LogInformation("Safety timer triggered: processing {WaitingCount} waiting requests",
                            _waitingRequests.Count);
                    }
                }

                UpdateSafetyTimerState();
            }

            // Only try processing queue if we haven't already determined there's no capacity
            // The flag will be reset when cleanup frees tokens, allowing processing to resume
            if (!_disposed && _waitingRequests.Count > 0 && !_hasInsufficientCapacity)
            {
                TryProcessingWaitingRequests();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Safety timer error");
        }
    }

    private bool HasCapacityInternal(int requiredTokens)
    {
        long currentUsage = GetCurrentUsageInternal();
        long effectiveLimit = _options.TokenLimit - _safetyBuffer;
        
        bool hasTokenCapacity = currentUsage + requiredTokens <= effectiveLimit;
        bool hasRequestCapacity = GetCurrentRequestCount() < _options.MaxRequestsPerMinute;
        
        return hasTokenCapacity && hasRequestCapacity;
    }

    private void RecordActualUsageCallback(TokenReservation reservation, int actualTokensUsed)
    {
        if (_disposed) return;

        lock (_lock)
        {
            // Update the actual usage in the active reservation
            if (_activeReservations.TryGetValue(reservation.Id, out var pendingReservation))
            {
                pendingReservation.ActualTokensUsed = actualTokensUsed;

                _logger.LogDebug("Recorded actual usage for reservation {ReservationId}: {ActualTokens} tokens (reserved: {ReservedTokens})",
                    reservation.Id, actualTokensUsed, reservation.ReservedTokens);
            }
        }
    }

    private Task ReleaseReservationAsync(TokenReservation reservation)
    {
        if (_disposed) return Task.CompletedTask;

        bool shouldProcessQueue = false;

        lock (_lock)
        {
            if (_activeReservations.Remove(reservation.Id))
            {
                shouldProcessQueue = _waitingRequests.Count > 0;

                // Reset insufficient capacity flag when reservation is released
                // This allows queue processing to resume even if cleanup hasn't run yet
                if (shouldProcessQueue)
                {
                    _hasInsufficientCapacity = false;
                }

                if (reservation.ActualTokensUsed.HasValue)
                {
                    var now =  DateTime.UtcNow;
                    _usageTimeline.Enqueue(new TokenUsageEntry(now, reservation.ActualTokensUsed.Value, reservation.ReservedTokens));
                    _currentActualUsage += reservation.ActualTokensUsed.Value;

                    var efficiency = reservation.ReservedTokens > 0
                        ? (double)reservation.ActualTokensUsed.Value / reservation.ReservedTokens * 100
                        : 100;

                    _logger.LogDebug("Released reservation {ReservationId}: {Reserved} -> {Actual} tokens ({Efficiency:F1}% efficiency), active reservations: {ActiveCount}",
                            reservation.Id, reservation.ReservedTokens, reservation.ActualTokensUsed.Value, efficiency, _activeReservations.Count);

                    // Record efficiency metric
                    _metrics?.RecordEfficiency(reservation.ActualTokensUsed.Value, reservation.ReservedTokens);
                }
                else
                {
                    _logger.LogDebug("Released failed reservation {ReservationId}: {Reserved} tokens (no usage recorded), active reservations: {ActiveCount}",
                        reservation.Id, reservation.ReservedTokens, _activeReservations.Count);
                }

                UpdateSafetyTimerState();
            }
        }

        if (shouldProcessQueue)
            TryProcessingWaitingRequests();

        return Task.CompletedTask;
    }

    // ============================================================================
    // MONITORING METHODS
    // ============================================================================
    
    public long GetCurrentUsage()
    {
        lock (_lock)
        {
            CleanupExpiredRecords();
            return GetCurrentUsageInternal();
        }
    }

    public TokenUsageStats GetUsageStats()
    {
        lock (_lock)
        {
            CleanupExpiredRecords();

            // CurrentUsage includes:
            // 1. Completed requests (from _usageTimeline)
            // 2. Active reservations with recorded actual usage
            // This provides real-time visibility into actual token consumption
            long completedUsage = _currentActualUsage;

            // Calculate actual usage from active reservations that have called RecordActualUsage()
            long activeActualUsage = _activeReservations.Values
                .Where(r => r.ActualTokensUsed.HasValue)
                .Sum(r => (long)r.ActualTokensUsed!.Value);

            long currentUsage = completedUsage + activeActualUsage;

            // ReservedTokens now represents ONLY estimated tokens for reservations that haven't called RecordActualUsage()
            // For reservations that have recorded actual usage, we use the actual value (already in currentUsage)
            long reservedTokens = _activeReservations.Values
                .Where(r => !r.ActualTokensUsed.HasValue)
                .Sum(r => (long)r.EstimatedTokens);

            long tokenLimit = _options.TokenLimit;
            long effectiveCapacity = tokenLimit - _safetyBuffer;

            // Available capacity accounts for both completed usage AND reserved/actual tokens
            long totalUsage = currentUsage + reservedTokens;
            long availableCapacity = Math.Max(0, effectiveCapacity - totalUsage);

            int activeReservationsCount = _activeReservations.Count;
            int waitingRequestsCount = _waitingRequests.Count;

            // Calculate average estimation efficiency from active reservations
            double averageEstimationEfficiency = CalculateAverageEstimationEfficiency();

            return new TokenUsageStats(
                currentUsage,
                reservedTokens,
                availableCapacity,
                effectiveCapacity,
                tokenLimit,
                activeReservationsCount,
                waitingRequestsCount,
                averageEstimationEfficiency);
        }
    }

    public long GetReservedTokens()
    {
        lock (_lock)
        {
            return GetReservedTokensInternal();
        }
    }

    // ============================================================================
    // EXPLICIT INTERFACE IMPLEMENTATIONS
    // ============================================================================

    async Task<ITokenReservation> ITokenRateGate.ReserveTokensAsync(int inputTokens, int estimatedOutputTokens, CancellationToken cancellationToken)
    {
        return await ReserveTokensAsync(inputTokens, estimatedOutputTokens, cancellationToken);
    }

    ITokenUsageStats ITokenRateGate.GetUsageStats()
    {
        return GetUsageStats();
    }

    private long GetCurrentUsageInternal()
    {
        // This method is used for capacity calculations
        // It includes: completed usage + actual usage from active + estimated from active (not yet recorded)
        long completedUsage = _currentActualUsage;

        // For each active reservation:
        // - If actual usage recorded: use actual
        // - If not recorded yet: use estimated
        long activeUsage = _activeReservations.Values.Sum(r =>
            r.ActualTokensUsed.HasValue ? (long)r.ActualTokensUsed.Value : (long)r.EstimatedTokens);

        return completedUsage + activeUsage;
    }

    private long GetReservedTokensInternal()
    {
        // Only count reservations that haven't recorded actual usage yet
        return _activeReservations.Values
            .Where(r => !r.ActualTokensUsed.HasValue)
            .Sum(r => (long)r.EstimatedTokens);
    }

    private int GetCurrentRequestCount()
    {
        // Since CleanupRequestTimeline removes old entries regularly (every 3 seconds),
        // we can use the direct count for performance. This is O(1) instead of O(n).
        // The cleanup ensures the queue contains mostly recent requests.
        return _requestTimeline.Count;
    }

    private double CalculateAverageEstimationEfficiency()
    {
        // Calculate average efficiency from completed requests in the usage timeline
        if (_usageTimeline.Count == 0)
            return 1.0; // Default to perfect efficiency when no data available

        int totalActual = 0;
        int totalReserved = 0;

        foreach (var entry in _usageTimeline)
        {
            totalActual += entry.ActualTokens;
            totalReserved += entry.ReservedTokens;
        }

        return totalReserved > 0 ? (double)totalActual / totalReserved : 1.0;
    }

    private void ValidateOptions()
    {
        if (_options.TokenLimit <= 0)
            throw new ArgumentException("TokenLimit must be positive");
        
        if (_options.WindowSeconds <= 0)
            throw new ArgumentException("WindowSeconds must be positive");

        if (_options.SafetyBufferPercentage < 0)
            throw new ArgumentException("SafetyBufferPercentage cannot be negative");

        if (_options.SafetyBufferPercentage >= 1.0)
            throw new ArgumentException("SafetyBufferPercentage must be less than 1.0 (100%)");

        if (_options.SafetyBufferPercentage > 0.5)
            _logger.LogWarning("SafetyBufferPercentage ({SafetyBufferPercentage:P0}) is more than 50% of TokenLimit. " +
                               "This leaves very little usable capacity and may cause frequent queuing.",
                _options.SafetyBufferPercentage);
        
        if (_options.MaxConcurrentRequests <= 0)
            throw new ArgumentException("MaxConcurrentRequests must be positive");

        if (_options.MaxConcurrentRequests > 10_000)
            throw new ArgumentException(
                $"MaxConcurrentRequests ({_options.MaxConcurrentRequests:N0}) exceeds safe limit (10,000). " +
                $"This could lead to resource exhaustion. The semaphore controls both active reservations " +
                $"and queued requests, so excessive values can cause memory exhaustion.");

        if (_options.MaxRequestsPerMinute <= 0)
            throw new ArgumentException("MaxRequestsPerMinute must be positive");

        if (_options.MaxWaitTime.HasValue)
        {
            if (_options.MaxWaitTime.Value <= TimeSpan.Zero)
                throw new ArgumentException("MaxWaitTime must be positive when set", nameof(_options.MaxWaitTime));

            if (_options.MaxWaitTime.Value > TimeSpan.FromHours(24))
                throw new ArgumentException("MaxWaitTime cannot exceed 24 hours for practical use", nameof(_options.MaxWaitTime));
        }
        
        if (_options.OutputMultiplier < 0)
            throw new ArgumentException("OutputMultiplier cannot be negative");
        
        if (_options.DefaultOutputTokens < 0)
            throw new ArgumentException("DefaultOutputTokens cannot be negative");
        
        if (_options.DefaultOutputTokens > _options.TokenLimit)
            throw new ArgumentException($"DefaultOutputTokens ({_options.DefaultOutputTokens}) cannot exceed TokenLimit ({_options.TokenLimit})", 
                nameof(_options.DefaultOutputTokens));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _safetyTimer.Dispose();
            _metrics?.Dispose();
            _concurrencyLimiter.Dispose();

            lock (_lock)
            {
                foreach (var request in _waitingRequests)
                {
                    request.TaskCompletionSource.TrySetCanceled();
                }
                _waitingRequests.Clear();
            }
        }
        
        GC.SuppressFinalize(this);
    }

    // ============================================================================
    // SUPPORTING TYPES
    // ============================================================================

    private record TokenUsageEntry(DateTime Timestamp, int ActualTokens, int ReservedTokens);
    private class PendingReservations
    {
        public Guid Id { get; }
        public int EstimatedTokens { get; }
        public DateTime Timestamp { get; }
        public int? ActualTokensUsed { get; set; }

        public PendingReservations(Guid id, int estimatedTokens, DateTime timestamp)
        {
            Id = id;
            EstimatedTokens = estimatedTokens;
            Timestamp = timestamp;
            ActualTokensUsed = null;
        }
    }
    private class WaitingRequest
    {
        public int RequiredTokens { get; }
        public CancellationToken CancellationToken { get; set; }
        public TaskCompletionSource<Guid> TaskCompletionSource { get; }
        public LinkedListNode<WaitingRequest>? Node { get; set; }
        public Guid ReservationId { get; set; }

        public WaitingRequest(int requiredTokens, CancellationToken cancellationToken)
        {
            RequiredTokens = requiredTokens;
            CancellationToken = cancellationToken;
            TaskCompletionSource = new TaskCompletionSource<Guid>();
        }
    }
}
