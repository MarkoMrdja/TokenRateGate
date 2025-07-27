using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.Core;

public class TokenRateGate
{
    // ============================================================================
    // CONFIGURATION AND DEPENDENCIES
    // ============================================================================

    private readonly TokenRateGateOptions _options;
    private readonly ILogger<TokenRateGate> _logger;

    private readonly object _lock = new();

    // ============================================================================
    // TOKEN TRACKING
    // ============================================================================

    // Completed requests timeline
    private readonly Queue<TokenUsageEntry> _usageTimeline = new();
    private int _currentActualUsage = 0;

    // Executing requests
    private readonly Dictionary<Guid, PendingReservations> _activeReservations = new();

    // Waiting requests
    private readonly LinkedList<WaitingRequests> _waitingRequests = new();

    // Request timeline for rate limiting
    private readonly Queue<DateTime> _requestTimeline = new();

    private readonly SemaphoreSlim _concurrencyLimiter;

    private readonly Timer _safetyTimer;
    private bool _disposed = false;

    private DateTime _lastCleanup = DateTime.MinValue;
    private readonly TimeSpan _internalOperationInterval = TimeSpan.FromSeconds(3); // TODO: Consider making this configurable

    // ============================================================================
    // CONSTRUCTORS
    // ============================================================================

    public TokenRateGate(IOptions<TokenRateGateOptions> options, ILogger<TokenRateGate> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);

        _safetyTimer = new Timer(SafetyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    public TokenRateGate(TokenRateGateOptions options, ILogger<TokenRateGate> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
        
        _safetyTimer = new Timer(SafetyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ============================================================================
    // MAIN IMPLEMENTATION
    // ============================================================================

    /*public async Task<TokenReservation> ReserveTokensAsync(int inputTokens, int estimatedOutputTokens = 0, CancellationToken cancellationToken = default) 
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGate));
        
        if (inputTokens <= 0)
            throw new ArgumentException("Input tokens must be positive", nameof(inputTokens));
        
        if (estimatedOutputTokens < 0)
            throw new ArgumentException("Estimated output tokens cannot be negative", nameof(estimatedOutputTokens));
        
        int totalEstimatedTokens = CalculateEstimatedTotalTokens(inputTokens, estimatedOutputTokens);
        
        await _concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            // Try immediate reservation, the fast path
            if (TryReserveImmediately(totalEstimatedTokens, out var immediateId))
            {
                _logger.LogDebug("Immediate reservation: {TotalTokens} tokens (input: {InputTokens}, estimated output: {EstimatedOutput} with ID {ReservationID}",
                    totalEstimatedTokens, inputTokens, totalEstimatedTokens - inputTokens, immediateId);
                
                UpdateSafetyTimerState();

                return new TokenReservation(immediateId, totalEstimatedTokens, inputTokens, ReleaseReservationAsync);
            }
        }
        catch
        {
            _concurrencyLimiter.Release();
            throw;
        }
    }*/

    private int CalculateEstimatedTotalTokens(int inputTokens, int estimatedOutputTokens)
    {
        if (estimatedOutputTokens > 0)
            return inputTokens + estimatedOutputTokens;

        if (_options.OutputEstimationStrategy == OutputEstimationStrategy.FixedMultiplier)
            return inputTokens + (int)Math.Ceiling(inputTokens * _options.OutputMultiplier);
        else if (_options.OutputEstimationStrategy == OutputEstimationStrategy.FixedAmount)
            return inputTokens + _options.DefaultOutputTokens;
        else
            return inputTokens * 2;
    }

    private bool TryReserveImmediately(int requiredTokens, out Guid immediateId)
    {
        lock (_lock)
        {
            return TryReserveImmediatelyInternal(requiredTokens, out immediateId);
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
    private void CleanupExpiredRecords()
    {
        var now =  DateTime.UtcNow;

        if (now - _lastCleanup < _internalOperationInterval)
            return;

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
            removedTokens += expired.Tokens;
            removedEntries++;
        }
        
        if (removedTokens > 0)
        {
            _currentActualUsage -= removedTokens;
            
            _logger.LogDebug("Cleaned {RemovedTokens} expired tokens ({RemovedEntries} entries). Current usage: {CurrentUsage}",
                             removedTokens, removedEntries, _currentActualUsage);
            
            return true;
        }
        
        return false;
    }

    private void CleanupRequestTimeline(DateTime now)
    {
        var requestHistoryWindow = TimeSpan.FromSeconds(Math.Max(120, _options.WindowSeconds * 2));
        var cutoff = now.Subtract(requestHistoryWindow);
        
        while (_requestTimeline.Count > 0 && _requestTimeline.Peek() < cutoff)
        {
            _requestTimeline.Dequeue();
        }
    }

    private void CleanupStaleReservations(DateTime now)
    {
        var staleTimeout = TimeSpan.FromSeconds(Math.Max(600, Math.Min(86400, _options.WindowSeconds * 10)));
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
        List<WaitingRequests>? grantedRequests;

        lock (_lock)
        {
            if (_waitingRequests.Count == 0)
            {
                UpdateSafetyTimerState();
                return;
            }
            
            CleanupExpiredRecords();
            
            var currentNode = _waitingRequests.First;
            grantedRequests = new List<WaitingRequests>();

            while (currentNode != null)
            {
                var request = currentNode.Value;
                var nextNode = currentNode.Next;

                if (request.CancellationToken.IsCancellationRequested)
                {
                    _waitingRequests.Remove(currentNode);
                    request.Node = null;
                    request.TaskCompletionSource.TrySetCanceled();
                    
                    _logger.LogDebug("Removed cancelled request: {RequiredTokens} tokens", request.RequiredTokens);
                }
                else if (TryReserveImmediately(request.RequiredTokens, out Guid reservationId))
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
                    _logger.LogDebug("Insufficient capacity for {RequiredTokens} tokens, stopping queue processing " +
                                     "(current usage: {CurrentUsage}/{TokenLimit}",
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
            _safetyTimer.Change(_internalOperationInterval, _internalOperationInterval);
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
                    CleanupExpiredRecords();
                    TryProcessingWaitingRequests();
                    UpdateSafetyTimerState();
                }
                else
                {
                    UpdateSafetyTimerState();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Safety timer error");
        }
    }

    private bool HasCapacityInternal(int requiredTokens)
    {
        int currentUsage = GetCurrentUsageInternal();
        int effectiveLimit = _options.TokenLimit - _options.SafetyBuffer;
        
        bool hasTokenCapacity = currentUsage + requiredTokens <= effectiveLimit;
        bool hasRequestCapacity = GetCurrentRequestCount() < _options.MaxRequestsPerMinute;
        
        return hasTokenCapacity && hasRequestCapacity;
    }

    private async Task ReleaseReservationAsync(TokenReservation reservation)
    {
        if (_disposed) return;

        bool reservationFound = false;
        bool shouldProcessQueue = false;

        try
        {
            lock (_lock)
            {
                if (_activeReservations.Remove(reservation.Id))
                {
                    reservationFound = true;
                    shouldProcessQueue = _waitingRequests.Count > 0;

                    if (reservation.ActualTokensUsed.HasValue)
                    {
                        var now =  DateTime.UtcNow;
                        _usageTimeline.Enqueue(new TokenUsageEntry(now, reservation.ActualTokensUsed.Value));
                        _currentActualUsage += reservation.ActualTokensUsed.Value;

                        var efficiency = reservation.ReservedTokens > 0
                            ? (double)reservation.ActualTokensUsed.Value / reservation.ReservedTokens * 100
                            : 100;
                        
                        _logger.LogDebug("Released reservation {ReservationId}: {Reserved} -> {Actual} tokens ({Efficiency:F1}% efficiency), active reservations: {ActiveCount}", 
                                reservation.Id, reservation.ReservedTokens, reservation.ActualTokensUsed.Value, efficiency, _activeReservations.Count);
                    }
                    else
                    {
                        _logger.LogDebug("Released failed reservation {ReservationId}: {Reserved} tokens (no usage recorded), active reservations: {ActiveCount}", 
                            reservation.Id, reservation.ReservedTokens, _activeReservations.Count);
                    }
                    
                    UpdateSafetyTimerState();
                }
            }
        }
        finally
        {
            if (reservationFound)
                _concurrencyLimiter.Release();
        }
        
        if (shouldProcessQueue)
            TryProcessingWaitingRequests();
    }

    // ============================================================================
    // MONITORING METHODS
    // ============================================================================
    
    public int GetCurrentUsage()
    {
        lock (_lock)
        {
            CleanupExpiredRecords();
            return GetCurrentUsageInternal();
        }
    }

    public int GetReservedTokens()
    {
        lock (_lock)
        {
            return GetReservedTokensInternal();
        }
    }
    
    private int GetCurrentUsageInternal()
    {
        return _currentActualUsage + GetReservedTokensInternal();
    }

    private int GetReservedTokensInternal()
    {
        return _activeReservations.Values.Sum(r => r.EstimatedTokens);
    }

    private int GetCurrentRequestCount()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(_options.WindowSeconds);
        return _requestTimeline.Count(t => t >= cutoff);
    }

    // ============================================================================
    // SUPPORTING TYPES
    // ============================================================================

    private record TokenUsageEntry(DateTime Timestamp, int Tokens);
    private record PendingReservations(Guid Id, int EstimatedTokens, DateTime Timestamp);
    private class WaitingRequests
    {
        public int RequiredTokens { get; }
        public CancellationToken CancellationToken { get; set; }
        public TaskCompletionSource<Guid> TaskCompletionSource { get; }
        public LinkedListNode<WaitingRequests>? Node { get; set; }
        public Guid ReservationId { get; set; }

        public WaitingRequests(int requiredTokens, CancellationToken cancellationToken)
        {
            RequiredTokens = requiredTokens;
            CancellationToken = cancellationToken;
            TaskCompletionSource = new TaskCompletionSource<Guid>();
        }
    }
}
