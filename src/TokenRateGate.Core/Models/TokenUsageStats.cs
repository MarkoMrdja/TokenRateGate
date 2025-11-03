using TokenRateGate.Abstractions;

namespace TokenRateGate.Core.Models;

/// <summary>
/// Provides statistics about current token usage and system capacity.
/// Used for monitoring, debugging, and capacity planning.
/// </summary>
public class TokenUsageStats : ITokenUsageStats
{
    /// <summary>
    /// Initializes a new instance of TokenUsageStats.
    /// </summary>
    public TokenUsageStats(
        long currentUsage,
        long totalReserved,
        long availableTokens,
        long effectiveCapacity,
        long tokenLimit,
        int activeReservationsCount,
        int waitingRequestsCount,
        double averageEstimationEfficiency)
    {
        CurrentUsage = currentUsage;
        TotalReserved = totalReserved;
        AvailableTokens = availableTokens;
        EffectiveCapacity = effectiveCapacity;
        TokenLimit = tokenLimit;
        ActiveReservationsCount = activeReservationsCount;
        WaitingRequestsCount = waitingRequestsCount;
        AverageEstimationEfficiency = averageEstimationEfficiency;
    }

    /// <summary>
    /// The current token usage from completed requests within the time window.
    /// </summary>
    public long CurrentUsage { get; }

    /// <summary>
    /// The total number of tokens currently reserved by active requests.
    /// </summary>
    public long TotalReserved { get; }

    /// <summary>
    /// The number of tokens available for new reservations.
    /// </summary>
    public long AvailableTokens { get; }

    /// <summary>
    /// The effective capacity after applying safety buffer (TokenLimit - SafetyBuffer).
    /// This is the maximum usable capacity for the system.
    /// </summary>
    public long EffectiveCapacity { get; }

    /// <summary>
    /// The configured token limit per time window.
    /// </summary>
    public long TokenLimit { get; }

    /// <summary>
    /// The current usage as a percentage of the token limit (0-100).
    /// Includes both completed usage and active reservations.
    /// </summary>
    public double UsagePercentage => TokenLimit > 0
        ? (double)(CurrentUsage + TotalReserved) / TokenLimit * 100.0
        : 0.0;

    /// <summary>
    /// The number of active reservations currently held.
    /// </summary>
    public int ActiveReservationsCount { get; }

    /// <summary>
    /// The number of requests currently waiting for capacity.
    /// </summary>
    public int WaitingRequestsCount { get; }

    /// <summary>
    /// The average efficiency of token estimation (actual tokens / reserved tokens).
    /// Values close to 1.0 indicate accurate estimation.
    /// Values significantly less than 1.0 indicate over-reservation.
    /// </summary>
    public double AverageEstimationEfficiency { get; }

    /// <summary>
    /// Indicates whether the system is approaching capacity limits.
    /// True when usage exceeds 80% of token limit.
    /// </summary>
    public bool IsNearCapacity => UsagePercentage > 80.0;
}