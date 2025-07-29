namespace TokenRateGate.Core.Models;

/// <summary>
/// Provides statistics about current token usage and system capacity.
/// Used for monitoring, debugging, and capacity planning.
/// </summary>
/// <param name="CurrentUsage">Total tokens currently in use (actual usage + reserved tokens)</param>
/// <param name="ReservedTokens">Tokens currently reserved by active requests</param>
/// <param name="AvailableTokens">Tokens available for new reservations (includes safety buffer consideration)</param>
/// <param name="ActiveReservations">Number of requests currently holding reservations</param>
/// <param name="RequestsInLastMinute">Number of requests made in the last 60 seconds</param>
public record TokenUsageStats(
    int CurrentUsage,
    int ReservedTokens,
    int AvailableTokens,
    int ActiveReservations,
    int RequestsInLastMinute
)
{
    /// <summary>
    /// Percentage of total capacity currently in use (0.0 to 100.0).
    /// Useful for monitoring system load and triggering alerts.
    /// </summary>
    public double UsagePercentage => CurrentUsage + AvailableTokens > 0 
        ? (double)CurrentUsage / (CurrentUsage + AvailableTokens) * 100.0 
        : 0.0;

    /// <summary>
    /// Indicates whether the system is approaching capacity limits.
    /// True when usage exceeds 80% of available capacity.
    /// </summary>
    public bool IsNearCapacity => UsagePercentage > 80.0;

    /// <summary>
    /// Estimated time until full capacity based on current reservation rate.
    /// Returns null if no recent reservations to base calculation on.
    /// </summary>
    public TimeSpan? EstimatedTimeToCapacity
    {
        get
        {
            if (RequestsInLastMinute == 0 || AvailableTokens <= 0)
                return null;

            // Simple linear projection based on recent request rate
            var tokensPerSecond = (double)RequestsInLastMinute / 60.0;
            var secondsToCapacity = AvailableTokens / tokensPerSecond;
            
            return TimeSpan.FromSeconds(secondsToCapacity);
        }
    }
}