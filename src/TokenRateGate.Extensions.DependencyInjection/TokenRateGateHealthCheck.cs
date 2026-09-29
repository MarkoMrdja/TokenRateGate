using Microsoft.Extensions.Diagnostics.HealthChecks;
using TokenRateGate.Abstractions;

namespace TokenRateGate.Extensions.DependencyInjection;

/// <summary>
/// Health check for TokenRateGate to monitor capacity and system health.
/// </summary>
public class TokenRateGateHealthCheck : IHealthCheck
{
    private readonly Core.TokenRateGate? _rateGate;
    private readonly ITokenRateGateFactory? _factory;
    private readonly double _degradedThreshold;
    private readonly double _unhealthyThreshold;

    /// <summary>
    /// Initializes a new instance of the TokenRateGateHealthCheck.
    /// </summary>
    /// <param name="rateGate">The TokenRateGate instance to monitor (optional for factory scenarios)</param>
    /// <param name="factory">The factory used to resolve named gates in factory scenarios (optional)</param>
    /// <param name="degradedThreshold">Usage percentage threshold for degraded status (default: 80%)</param>
    /// <param name="unhealthyThreshold">Usage percentage threshold for unhealthy status (default: 95%)</param>
    public TokenRateGateHealthCheck(
        Core.TokenRateGate? rateGate = null,
        ITokenRateGateFactory? factory = null,
        double degradedThreshold = 80.0,
        double unhealthyThreshold = 95.0)
    {
        _rateGate = rateGate;
        _factory = factory;
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
        if (_rateGate == null && _factory == null)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "No TokenRateGate instance registered (likely using factory pattern)"));
        }

        try
        {
            if (_rateGate != null)
            {
                var stats = _rateGate.GetUsageStats();

                var data = new Dictionary<string, object>
                {
                    ["currentUsage"] = stats.CurrentUsage,
                    ["reservedTokens"] = stats.TotalReserved,
                    ["availableTokens"] = stats.AvailableTokens,
                    ["activeReservations"] = stats.ActiveReservationsCount,
                    ["waitingRequests"] = stats.WaitingRequestsCount,
                    ["usagePercentage"] = stats.UsagePercentage
                };

                return Task.FromResult(Evaluate(stats.UsagePercentage, data));
            }

            // Factory scenario: evaluate every gate created by the factory and report the worst usage.
            double worstUsagePercentage = 0;
            var gateCount = 0;
            long totalCurrentUsage = 0;
            long totalReservedTokens = 0;
            long totalAvailableTokens = 0;
            var totalActiveReservations = 0;
            var totalWaitingRequests = 0;

            foreach (var name in _factory!.GetAllNames())
            {
                var gate = _factory.Get(name);
                if (gate == null)
                    continue;

                var stats = gate.GetUsageStats();
                gateCount++;

                totalCurrentUsage += stats.CurrentUsage;
                totalReservedTokens += stats.TotalReserved;
                totalAvailableTokens += stats.AvailableTokens;
                totalActiveReservations += stats.ActiveReservationsCount;
                totalWaitingRequests += stats.WaitingRequestsCount;

                if (stats.UsagePercentage > worstUsagePercentage)
                    worstUsagePercentage = stats.UsagePercentage;
            }

            if (gateCount == 0)
            {
                return Task.FromResult(HealthCheckResult.Healthy(
                    "No TokenRateGate instances created by the factory yet"));
            }

            var aggregateData = new Dictionary<string, object>
            {
                ["gateCount"] = gateCount,
                ["currentUsage"] = totalCurrentUsage,
                ["reservedTokens"] = totalReservedTokens,
                ["availableTokens"] = totalAvailableTokens,
                ["activeReservations"] = totalActiveReservations,
                ["waitingRequests"] = totalWaitingRequests,
                ["worstUsagePercentage"] = worstUsagePercentage
            };

            return Task.FromResult(Evaluate(worstUsagePercentage, aggregateData));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Failed to retrieve TokenRateGate statistics",
                exception: ex));
        }
    }

    /// <summary>
    /// Determines the health status from a usage percentage.
    /// </summary>
    /// <param name="usagePercentage">The usage percentage to evaluate</param>
    /// <param name="data">The data to attach to the resulting health check entry</param>
    /// <returns>The health check result for the given usage percentage</returns>
    private HealthCheckResult Evaluate(double usagePercentage, Dictionary<string, object> data)
    {
        // Determine health status based on usage percentage
        if (usagePercentage >= _unhealthyThreshold)
        {
            return HealthCheckResult.Unhealthy(
                $"Token usage is critically high: {usagePercentage:F1}% (threshold: {_unhealthyThreshold}%)",
                data: data);
        }

        if (usagePercentage >= _degradedThreshold)
        {
            return HealthCheckResult.Degraded(
                $"Token usage is elevated: {usagePercentage:F1}% (threshold: {_degradedThreshold}%)",
                data: data);
        }

        return HealthCheckResult.Healthy(
            $"Token usage is normal: {usagePercentage:F1}%",
            data: data);
    }
}
