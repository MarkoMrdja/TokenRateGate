using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Core.Capacity;

/// <summary>
/// Calculates token and request capacity based on current state.
/// Thread-safe through immutable state snapshots.
/// </summary>
/// <remarks>
/// This class provides pure calculation methods for determining capacity availability.
/// It does not maintain any state itself, instead accepting state snapshots as parameters.
/// All calculations are thread-safe as they operate on parameters passed in, not shared state.
/// </remarks>
internal sealed class CapacityCalculator
{
    private readonly TokenRateGateOptions _options;
    private readonly int _safetyBuffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapacityCalculator"/> class.
    /// </summary>
    /// <param name="options">The rate gate configuration options.</param>
    /// <param name="safetyBuffer">The safety buffer in tokens (pre-calculated from options).</param>
    public CapacityCalculator(TokenRateGateOptions options, int safetyBuffer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _safetyBuffer = safetyBuffer;
    }

    /// <summary>
    /// Calculates current token usage from completed and active usage.
    /// </summary>
    /// <param name="completedUsage">The total tokens from completed requests still in the window.</param>
    /// <param name="activeReservations">Currently active reservations.</param>
    /// <returns>Total current token usage (completed + active).</returns>
    /// <remarks>
    /// For each active reservation:
    /// - If actual usage has been recorded: uses actual tokens
    /// - If not recorded yet: uses estimated tokens
    /// This provides an accurate picture of current capacity consumption.
    /// </remarks>
    public long CalculateCurrentUsage(
        long completedUsage,
        IEnumerable<PendingReservations> activeReservations)
    {
        long activeUsage = activeReservations.Sum(r =>
            r.ActualTokensUsed.HasValue ? (long)r.ActualTokensUsed.Value : (long)r.EstimatedTokens);

        return completedUsage + activeUsage;
    }

    /// <summary>
    /// Calculates reserved tokens (not yet recorded as actual).
    /// </summary>
    /// <param name="activeReservations">Currently active reservations.</param>
    /// <returns>Total tokens reserved but not yet recorded.</returns>
    /// <remarks>
    /// Only counts reservations where actual usage has not been recorded yet.
    /// Once actual usage is recorded, those tokens move from "reserved" to "actual" in statistics.
    /// </remarks>
    public long CalculateReservedTokens(IEnumerable<PendingReservations> activeReservations)
    {
        return activeReservations
            .Where(r => !r.ActualTokensUsed.HasValue)
            .Sum(r => (long)r.EstimatedTokens);
    }

    /// <summary>
    /// Checks if capacity is available for the required tokens.
    /// </summary>
    /// <param name="currentUsage">Current total token usage.</param>
    /// <param name="currentRequestCount">Current number of requests in the RPM window.</param>
    /// <param name="requiredTokens">Tokens needed for the new request.</param>
    /// <returns><c>true</c> if both token and request capacity are available; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Capacity is available only when BOTH conditions are met:
    /// 1. Token capacity: current usage + required tokens ≤ effective limit
    /// 2. Request capacity: current request count &lt; max requests per minute
    /// The effective limit is the configured token limit minus the safety buffer.
    /// </remarks>
    public bool HasCapacity(
        long currentUsage,
        int currentRequestCount,
        int requiredTokens)
    {
        long effectiveLimit = _options.TokenLimit - _safetyBuffer;

        bool hasTokenCapacity = currentUsage + requiredTokens <= effectiveLimit;
        bool hasRequestCapacity = currentRequestCount < _options.MaxRequestsPerMinute;

        return hasTokenCapacity && hasRequestCapacity;
    }

    /// <summary>
    /// Gets the effective token limit after applying the safety buffer.
    /// </summary>
    /// <returns>The effective token limit.</returns>
    /// <remarks>
    /// The effective limit is the configured TokenLimit minus the SafetyBuffer.
    /// This is the actual usable capacity for requests.
    /// </remarks>
    public long GetEffectiveLimit() => _options.TokenLimit - _safetyBuffer;
}
