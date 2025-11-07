using Microsoft.Extensions.Logging;
using TokenRateGate.Core.Abstractions;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Core.Timeline;

/// <summary>
/// Manages token and request timelines with automatic cleanup.
/// Thread-safety: This class is NOT thread-safe on its own. The caller (TokenRateGate) must synchronize access.
/// </summary>
internal sealed class TokenTimelineManager
{
    private readonly TokenRateGateOptions _options;
    private readonly ISystemClock _clock;
    private readonly ILogger _logger;

    private readonly Queue<TokenUsageEntry> _usageTimeline = new();
    private readonly Queue<DateTime> _requestTimeline = new();

    private long _currentActualUsage;

    // Constants for stale reservation cleanup
    private const int MinStaleReservationTimeoutSeconds = 600;    // 10 minutes
    private const int MaxStaleReservationTimeoutSeconds = 86400;  // 24 hours
    private const int StaleReservationTimeoutMultiplier = 10;

    public TokenTimelineManager(
        TokenRateGateOptions options,
        ISystemClock clock,
        ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the current actual token usage from completed requests.
    /// </summary>
    public long CurrentActualUsage => _currentActualUsage;

    /// <summary>
    /// Gets the current number of requests in the tracking window.
    /// </summary>
    public int RequestCount => _requestTimeline.Count;

    /// <summary>
    /// Gets the number of token usage entries in the timeline.
    /// </summary>
    public int TokenUsageCount => _usageTimeline.Count;

    /// <summary>
    /// Adds a completed token usage entry to the timeline.
    /// </summary>
    /// <param name="actualTokens">The actual number of tokens used by the request.</param>
    /// <param name="reservedTokens">The number of tokens that were reserved for the request.</param>
    public void AddTokenUsage(int actualTokens, int reservedTokens)
    {
        var entry = new TokenUsageEntry(_clock.UtcNow, actualTokens, reservedTokens);
        _usageTimeline.Enqueue(entry);
        _currentActualUsage += actualTokens;
    }

    /// <summary>
    /// Records a new request timestamp for RPM tracking.
    /// </summary>
    public void AddRequest()
    {
        _requestTimeline.Enqueue(_clock.UtcNow);
    }

    /// <summary>
    /// Removes expired entries from all timelines.
    /// </summary>
    /// <returns>A result indicating what was freed during cleanup.</returns>
    public CleanupResult CleanupExpiredEntries()
    {
        var now = _clock.UtcNow;

        var (tokensFreed, removedTokens) = CleanupTokenTimeline(now);
        var (requestsFreed, removedRequests) = CleanupRequestTimeline(now);

        return new CleanupResult(
            tokensFreed,
            requestsFreed,
            removedTokens,
            removedRequests,
            0);
    }

    /// <summary>
    /// Removes stale reservations older than the calculated timeout.
    /// </summary>
    /// <param name="activeReservations">The dictionary of active reservations to clean.</param>
    /// <returns>The number of stale reservations removed.</returns>
    public int CleanupStaleReservations(Dictionary<Guid, PendingReservations> activeReservations)
    {
        ArgumentNullException.ThrowIfNull(activeReservations);

        var now = _clock.UtcNow;
        var staleTimeout = CalculateStaleTimeout();
        var cutoff = now.Subtract(staleTimeout);

        var staleIds = activeReservations
            .Where(kv => kv.Value.Timestamp < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            activeReservations.Remove(id);
            _logger.LogWarning("Removed stale reservation {ReservationId}", id);
        }

        return staleIds.Count;
    }

    private (bool freed, int removed) CleanupTokenTimeline(DateTime now)
    {
        var cutoff = now.AddSeconds(-_options.WindowSeconds);
        int removedTokens = 0;
        int removedEntries = 0;

        while (_usageTimeline.Count > 0 && _usageTimeline.Peek().Timestamp < cutoff)
        {
            var expired = _usageTimeline.Dequeue();
            removedTokens += expired.ActualTokens;
            removedEntries++;
        }

        if (removedTokens > 0)
        {
            _currentActualUsage -= removedTokens;

            _logger.LogDebug(
                "Cleaned {RemovedTokens} expired tokens ({RemovedEntries} entries). Current usage: {CurrentUsage}",
                removedTokens, removedEntries, _currentActualUsage);

            return (true, removedTokens);
        }

        return (false, 0);
    }

    private (bool freed, int removed) CleanupRequestTimeline(DateTime now)
    {
        var cutoff = now.AddSeconds(-_options.RequestWindowSeconds);
        int removedRequests = 0;

        while (_requestTimeline.Count > 0 && _requestTimeline.Peek() < cutoff)
        {
            _requestTimeline.Dequeue();
            removedRequests++;
        }

        if (removedRequests > 0)
        {
            _logger.LogDebug("Cleaned up {RemovedRequests} expired request timestamps", removedRequests);
            return (true, removedRequests);
        }

        return (false, 0);
    }

    /// <summary>
    /// Calculates the average estimation efficiency from the usage timeline.
    /// </summary>
    /// <returns>The ratio of actual tokens to reserved tokens (1.0 = perfect estimation).</returns>
    public double CalculateAverageEstimationEfficiency()
    {
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

    private TimeSpan CalculateStaleTimeout()
    {
        var seconds = Math.Max(MinStaleReservationTimeoutSeconds,
            Math.Min(MaxStaleReservationTimeoutSeconds,
                _options.WindowSeconds * StaleReservationTimeoutMultiplier));

        return TimeSpan.FromSeconds(seconds);
    }
}
