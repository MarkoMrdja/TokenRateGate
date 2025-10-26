using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TokenRateGate.Extensions.DependencyInjection;

/// <summary>
/// Health check for TokenRateGate to monitor capacity and system health.
/// </summary>
public class TokenRateGateHealthCheck : IHealthCheck
{
    private readonly Core.TokenRateGate? _rateGate;
    private readonly double _degradedThreshold;
    private readonly double _unhealthyThreshold;

    /// <summary>
    /// Initializes a new instance of the TokenRateGateHealthCheck.
    /// </summary>
    /// <param name="rateGate">The TokenRateGate instance to monitor (optional for factory scenarios)</param>
    /// <param name="degradedThreshold">Usage percentage threshold for degraded status (default: 80%)</param>
    /// <param name="unhealthyThreshold">Usage percentage threshold for unhealthy status (default: 95%)</param>
    public TokenRateGateHealthCheck(
        Core.TokenRateGate? rateGate = null,
        double degradedThreshold = 80.0,
        double unhealthyThreshold = 95.0)
    {
        _rateGate = rateGate;
        _degradedThreshold = degradedThreshold;
        _unhealthyThreshold = unhealthyThreshold;

        if (degradedThreshold < 0 || degradedThreshold > 100)
            throw new ArgumentOutOfRangeException(nameof(degradedThreshold), "Threshold must be between 0 and 100");

        if (unhealthyThreshold < 0 || unhealthyThreshold > 100)
            throw new ArgumentOutOfRangeException(nameof(unhealthyThreshold), "Threshold must be between 0 and 100");

        if (degradedThreshold >= unhealthyThreshold)
            throw new ArgumentException("Degraded threshold must be less than unhealthy threshold");
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_rateGate == null)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "No TokenRateGate instance registered (likely using factory pattern)"));
        }

        try
        {
            var stats = _rateGate.GetUsageStats();

            var data = new Dictionary<string, object>
            {
                ["currentUsage"] = stats.CurrentUsage,
                ["reservedTokens"] = stats.TotalReserved,
                ["availableTokens"] = stats.AvailableCapacity,
                ["activeReservations"] = stats.ActiveReservationsCount,
                ["waitingRequests"] = stats.WaitingRequestsCount,
                ["usagePercentage"] = stats.UsagePercentage
            };

            // Determine health status based on usage percentage
            if (stats.UsagePercentage >= _unhealthyThreshold)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Token usage is critically high: {stats.UsagePercentage:F1}% (threshold: {_unhealthyThreshold}%)",
                    data: data));
            }

            if (stats.UsagePercentage >= _degradedThreshold)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Token usage is elevated: {stats.UsagePercentage:F1}% (threshold: {_degradedThreshold}%)",
                    data: data));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Token usage is normal: {stats.UsagePercentage:F1}%",
                data: data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Failed to retrieve TokenRateGate statistics",
                exception: ex));
        }
    }
}
