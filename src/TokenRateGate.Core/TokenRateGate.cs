using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Abstractions;
using TokenRateGate.Core.Capacity;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Timeline;
using TokenRateGate.Core.TokenEstimation;
using TokenRateGate.Core.Utils;
using TokenRateGate.Core.Validation;

namespace TokenRateGate.Core;

/// <summary>
/// Controls token-based rate limiting for LLM API requests.
/// Manages token reservations, queuing, and actual usage tracking to prevent exceeding API provider limits.
/// </summary>
/// <remarks>
/// TokenRateGate prevents exceeding both tokens-per-minute (TPM) and requests-per-minute (RPM) limits
/// by managing a sliding window of token usage. It queues requests when capacity is exhausted and
/// automatically processes them as capacity becomes available.
///
/// Key features:
/// - Sliding window rate limiting for accurate capacity tracking
/// - Automatic request queuing when limits are reached
/// - Supports both token and request rate limits
/// - Thread-safe for concurrent operations
/// - Configurable safety buffers and concurrency limits
/// </remarks>
/// <example>
/// <code>
/// var options = new TokenRateGateOptions
/// {
///     TokenLimit = 100_000, // 100K tokens per minute
///     WindowSeconds = 60,
///     MaxConcurrentRequests = 50
/// };
///
/// using var gate = new TokenRateGate(options, logger);
/// var reservation = await gate.ReserveTokensAsync(inputTokens: 500, estimatedOutputTokens: 1500);
/// await using var _ = reservation;
///
/// // Make your API call here
/// var response = await callLlmApi();
///
/// // Record actual usage for accurate tracking
/// reservation.RecordActualUsage(response.InputTokens, response.OutputTokens);
/// </code>
/// </example>
public class TokenRateGate : ITokenRateGate, IDisposable
{
    // ============================================================================
    // CONFIGURATION AND DEPENDENCIES
    // ============================================================================

    private readonly TokenRateGateOptions _options;
    private readonly ILogger<TokenRateGate> _logger;
    private readonly TokenRateGateMetrics? _metrics;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly CapacityCalculator _capacityCalculator;
    private readonly ISystemClock _clock;
    private readonly TokenTimelineManager _timelineManager;

    private readonly int _safetyBuffer; // Calculated from SafetyBufferPercentage

    private readonly object _lock = new();

    // ============================================================================
    // TOKEN TRACKING
    // ============================================================================

    // Executing requests
    private readonly Dictionary<Guid, PendingReservations> _activeReservations = new();

    // Waiting requests
    private readonly LinkedList<WaitingRequest> _waitingRequests = new();

    private readonly SemaphoreSlim _concurrencyLimiter;

    private readonly Timer _safetyTimer;
    private volatile bool _disposed;

    private DateTime _lastCleanup = DateTime.MinValue;

    /// <summary>
    /// Flag to avoid wasteful queue processing when capacity is known to be insufficient.
    /// Reset when tokens are freed during cleanup.
    /// Marked volatile because it's written under lock but read without lock in SafetyTimerCallback.
    /// This ensures memory visibility across threads - the timer thread always sees the latest value.
    /// </summary>
    private volatile bool _hasInsufficientCapacity = false;

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

    // ============================================================================
    // CONSTRUCTORS
    // ============================================================================

    public TokenRateGate(IOptions<TokenRateGateOptions> options, ILogger<TokenRateGate> logger)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
    }

    public TokenRateGate(TokenRateGateOptions options, ILogger<TokenRateGate> logger)
        : this(options, logger, new SystemClock())
    {
    }

    // Internal constructor for testing with injectable clock
    internal TokenRateGate(
        TokenRateGateOptions options,
        ILogger<TokenRateGate> logger,
        ISystemClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        TokenRateGateOptionsValidator.Validate(_options, _logger);

        // Initialize token estimator (use provided or default to CharacterBasedTokenEstimator)
        _tokenEstimator = _options.TokenEstimator ?? new CharacterBasedTokenEstimator();

        // Calculate safety buffer from percentage
        _safetyBuffer = (int)(_options.TokenLimit * _options.SafetyBufferPercentage);

        // Initialize capacity calculator
        _capacityCalculator = new CapacityCalculator(_options, _safetyBuffer);

        // Initialize timeline manager
        _timelineManager = new TokenTimelineManager(_options, _clock, _logger);

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);

        _safetyTimer = new Timer(SafetyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

        // Initialize metrics if OpenTelemetry is enabled
        _metrics = new TokenRateGateMetrics(this);
    }

    // ============================================================================
    // MAIN IMPLEMENTATION
    // ============================================================================

    /// <summary>
    /// Reserves capacity for a token-consuming operation, waiting if necessary until capacity is available.
    /// </summary>
    /// <param name="inputTokens">The number of input tokens that will be consumed.</param>
    /// <param name="estimatedOutputTokens">
    /// Estimated output tokens for the operation. If not provided, will be calculated based on
    /// <see cref="TokenRateGateOptions.OutputEstimationStrategy"/>.
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the reservation attempt.</param>
    /// <returns>
    /// A <see cref="TokenReservation"/> that must be disposed after the operation completes.
    /// Call <see cref="TokenReservation.RecordActualUsage"/> with actual token counts for accurate tracking.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown if the TokenRateGate has been disposed.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if inputTokens is not positive, estimatedOutputTokens is negative,
    /// or the total requested tokens exceeds the effective capacity.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the cancellationToken or exceeds <see cref="TokenRateGateOptions.MaxWaitTime"/>.
    /// </exception>
    /// <remarks>
    /// This method may queue the request if capacity is currently exhausted. The request will automatically
    /// proceed once capacity becomes available within the configured <see cref="TokenRateGateOptions.MaxWaitTime"/>.
    /// Always call <see cref="TokenReservation.RecordActualUsage"/> after your API call completes to ensure
    /// accurate capacity tracking.
    /// </remarks>
    public async Task<TokenReservation> ReserveTokensAsync(int inputTokens, int? estimatedOutputTokens = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGate));

        if (inputTokens <= 0)
            throw new ArgumentException("Input tokens must be positive", nameof(inputTokens));

        if (estimatedOutputTokens.HasValue && estimatedOutputTokens.Value < 0)
            throw new ArgumentException("Estimated output tokens cannot be negative", nameof(estimatedOutputTokens));

        int totalEstimatedTokens = CalculateEstimatedTotalTokens(inputTokens, estimatedOutputTokens);

        // Record reservation request metric
        _metrics?.RecordReservationRequested();

        // Validate that the request is possible given the token limit and safety buffer
        int effectiveLimit = _options.TokenLimit - _safetyBuffer;
        if (totalEstimatedTokens > effectiveLimit)
        {
            throw new InvalidOperationException(
                $"Requested tokens ({totalEstimatedTokens:N0}) exceeds effective capacity ({effectiveLimit:N0}). " +
                $"Token limit: {_options.TokenLimit:N0}, Safety buffer: {_safetyBuffer:N0} ({_options.SafetyBufferPercentage:P0}). " +
                $"This request can never be fulfilled with current configuration. Either reduce the request size or increase TokenLimit.");
        }

        // Start timing from the very beginning of the reservation attempt
        var reservationStartTime = _clock.UtcNow;

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
                            // Only cancel if still in queue (node not null and still belongs to this list)
                            if (waitingRequest.Node != null && waitingRequest.Node.List == _waitingRequests)
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

                    // Transfer semaphore ownership immediately after grant to prevent double-release race condition
                    // If an exception occurs after this point, TokenReservation will handle the release
                    semaphoreAcquired = false; // Ownership transferred to TokenReservation

                    var totalWaitTime = _clock.UtcNow - reservationStartTime;
                    _logger.LogDebug(
                        "Granted queued reservation: {TotalTokens} tokens with ID {ReservationId} after waiting {WaitTime:mm\\:ss}",
                        totalEstimatedTokens, reservationId, totalWaitTime);

                    // Record successful grant after queue wait and wait time
                    _metrics?.RecordReservationGranted();
                    _metrics?.RecordWaitTime(totalWaitTime);

                    return new TokenReservation(reservationId, totalEstimatedTokens, inputTokens, ReleaseReservationAsync, _concurrencyLimiter, RecordActualUsageCallback);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested &&
                                                            !cancellationToken.IsCancellationRequested)
                {
                    var elapsedTime = _clock.UtcNow - reservationStartTime;

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

    private int CalculateEstimatedTotalTokens(int inputTokens, int? estimatedOutputTokens)
    {
        long total;

        // If user explicitly provided estimatedOutputTokens (including 0), use it directly
        // Otherwise, apply the configured estimation strategy
        if (estimatedOutputTokens.HasValue)
        {
            total = (long)inputTokens + estimatedOutputTokens.Value;
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
        else // Conservative strategy
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
            var reservation = new PendingReservations(immediateId, requiredTokens, _clock.UtcNow);
            _activeReservations[immediateId] = reservation;

            _timelineManager.AddRequest();

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
        var now = _clock.UtcNow;

        // Throttling removed for high-throughput performance
        // Cleanup is fast (O(k) where k = expired items) and lock is already held

        var result = _timelineManager.CleanupExpiredEntries();
        _timelineManager.CleanupStaleReservations(_activeReservations);

        _lastCleanup = now;

        // Reset insufficient capacity flag if tokens or requests were freed
        if (result.TokensFreed || result.RequestsFreed)
        {
            _hasInsufficientCapacity = false;
        }

        // Process waiting queue if either token capacity or request capacity was freed
        if ((result.TokensFreed || result.RequestsFreed) && _waitingRequests.Count > 0)
            TryProcessingWaitingRequests();
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

        // Run timer when we have waiting requests - handles two scenarios:
        // 1. No active work (deadlock detection - all requests done but timestamps haven't expired)
        // 2. Active work exists but RPM limit blocks new requests (timestamps need to expire)
        bool shouldRunTimer = hasWaitingRequests;

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
                if (_waitingRequests.Count > 0)
                {
                    // Always cleanup to free expired tokens/requests (throttling removed for performance)
                    // This handles two scenarios:
                    // 1. No active reservations: deadlock detection (all work done but timestamps haven't expired)
                    // 2. Active reservations exist: RPM limit blocking (request timestamps need to expire)
                    CleanupExpiredRecords(forceCleanup: true);

                    // Only log if we're going to try processing (avoid log spam)
                    if (!_hasInsufficientCapacity)
                    {
                        _logger.LogInformation("Safety timer triggered: processing {WaitingCount} waiting requests (active: {ActiveCount})",
                            _waitingRequests.Count, _activeReservations.Count);
                    }
                }

                UpdateSafetyTimerState();
            }

            // Only try processing queue if we haven't already determined there's no capacity
            // The flag will be reset when cleanup frees tokens or requests, allowing processing to resume
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
        int requestCount = _timelineManager.RequestCount;

        return _capacityCalculator.HasCapacity(currentUsage, requestCount, requiredTokens);
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
                    _timelineManager.AddTokenUsage(reservation.ActualTokensUsed.Value, reservation.ReservedTokens);

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

    /// <summary>
    /// Retrieves current usage statistics for monitoring and capacity planning.
    /// </summary>
    /// <returns>
    /// A <see cref="TokenUsageStats"/> instance containing current usage, available capacity,
    /// active reservations, waiting requests, and other metrics.
    /// </returns>
    /// <remarks>
    /// This method is thread-safe and provides a point-in-time snapshot of the system state.
    /// Use this for monitoring dashboards, logging, or dynamic capacity planning.
    /// </remarks>
    public TokenUsageStats GetUsageStats()
    {
        lock (_lock)
        {
            CleanupExpiredRecords();

            // CurrentUsage includes:
            // 1. Completed requests (from timeline manager)
            // 2. Active reservations with recorded actual usage
            // This provides real-time visibility into actual token consumption
            long completedUsage = _timelineManager.CurrentActualUsage;

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
        return _capacityCalculator.CalculateCurrentUsage(
            _timelineManager.CurrentActualUsage,
            _activeReservations.Values);
    }

    private long GetReservedTokensInternal()
    {
        return _capacityCalculator.CalculateReservedTokens(_activeReservations.Values);
    }

    /// <summary>
    /// Gets the current number of requests in the RPM tracking window.
    /// </summary>
    /// <returns>The count of requests (both active and completed) within the RPM window.</returns>
    /// <remarks>
    /// This method returns the count of all requests that fall within the configured RequestWindowSeconds,
    /// including both active reservations and completed requests that haven't yet expired from the timeline.
    /// This is useful for monitoring how close you are to hitting the MaxRequestsPerMinute limit.
    /// Since CleanupRequestTimeline removes old entries regularly, we can use the direct count for performance.
    /// This is O(1) instead of O(n). The cleanup ensures the queue contains mostly recent requests.
    /// </remarks>
    public int GetCurrentRequestCount()
    {
        return _timelineManager.RequestCount;
    }

    private double CalculateAverageEstimationEfficiency()
    {
        return _timelineManager.CalculateAverageEstimationEfficiency();
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

}
