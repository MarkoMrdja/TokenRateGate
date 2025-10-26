using System.Diagnostics.Metrics;

namespace TokenRateGate.Core;

/// <summary>
/// Provides OpenTelemetry metrics instrumentation for TokenRateGate.
/// Enables production observability and monitoring of rate limiting behavior.
/// </summary>
internal sealed class TokenRateGateMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly TokenRateGate _rateGate;

    // Counters
    private readonly Counter<long> _reservationsRequested;
    private readonly Counter<long> _reservationsGranted;
    private readonly Counter<long> _reservationsRejected;
    private readonly Counter<long> _reservationsTimeout;
    private readonly Counter<long> _reservationsCancelled;

    // Histograms
    private readonly Histogram<double> _reservationWaitTime;
    private readonly Histogram<double> _reservationEfficiency;

    // Observable Gauges (registered in constructor)
    private readonly ObservableGauge<double> _tokenUsagePercentage;
    private readonly ObservableGauge<int> _tokensAvailable;
    private readonly ObservableGauge<int> _activeReservationsCount;
    private readonly ObservableGauge<int> _waitingRequestsCount;

    public TokenRateGateMetrics(TokenRateGate rateGate, string? instanceName = null)
    {
        _rateGate = rateGate ?? throw new ArgumentNullException(nameof(rateGate));

        // Create meter with optional instance name for multi-tenant scenarios
        var meterName = string.IsNullOrEmpty(instanceName)
            ? "TokenRateGate"
            : $"TokenRateGate.{instanceName}";

        _meter = new Meter(meterName, "1.0.0");

        // Initialize counters
        _reservationsRequested = _meter.CreateCounter<long>(
            "token_reservations_requested",
            unit: "reservations",
            description: "Total number of token reservation attempts");

        _reservationsGranted = _meter.CreateCounter<long>(
            "token_reservations_granted",
            unit: "reservations",
            description: "Number of successfully granted token reservations");

        _reservationsRejected = _meter.CreateCounter<long>(
            "token_reservations_rejected",
            unit: "reservations",
            description: "Number of rejected reservations due to capacity exhaustion");

        _reservationsTimeout = _meter.CreateCounter<long>(
            "token_reservations_timeout",
            unit: "reservations",
            description: "Number of reservations that timed out waiting for capacity");

        _reservationsCancelled = _meter.CreateCounter<long>(
            "token_reservations_cancelled",
            unit: "reservations",
            description: "Number of reservations cancelled by user");

        // Initialize histograms
        _reservationWaitTime = _meter.CreateHistogram<double>(
            "token_reservation_wait_time_ms",
            unit: "ms",
            description: "Time spent waiting in queue for token capacity");

        _reservationEfficiency = _meter.CreateHistogram<double>(
            "token_reservation_efficiency",
            unit: "ratio",
            description: "Ratio of actual tokens used to reserved tokens (1.0 = perfect estimation)");

        // Initialize observable gauges
        _tokenUsagePercentage = _meter.CreateObservableGauge(
            "token_usage_percentage",
            observeValue: () => GetTokenUsagePercentage(),
            unit: "%",
            description: "Current token capacity utilization percentage");

        _tokensAvailable = _meter.CreateObservableGauge(
            "tokens_available",
            observeValue: () => GetTokensAvailable(),
            unit: "tokens",
            description: "Number of tokens currently available for new reservations");

        _activeReservationsCount = _meter.CreateObservableGauge(
            "active_reservations_count",
            observeValue: () => GetActiveReservationsCount(),
            unit: "reservations",
            description: "Number of active token reservations currently held");

        _waitingRequestsCount = _meter.CreateObservableGauge(
            "waiting_requests_count",
            observeValue: () => GetWaitingRequestsCount(),
            unit: "requests",
            description: "Number of requests currently waiting for token capacity");
    }

    // Counter recording methods
    public void RecordReservationRequested() => _reservationsRequested.Add(1);
    public void RecordReservationGranted() => _reservationsGranted.Add(1);
    public void RecordReservationRejected() => _reservationsRejected.Add(1);
    public void RecordReservationTimeout() => _reservationsTimeout.Add(1);
    public void RecordReservationCancelled() => _reservationsCancelled.Add(1);

    // Histogram recording methods
    public void RecordWaitTime(TimeSpan waitTime)
    {
        _reservationWaitTime.Record(waitTime.TotalMilliseconds);
    }

    public void RecordEfficiency(int actualTokens, int reservedTokens)
    {
        if (reservedTokens > 0)
        {
            var efficiency = (double)actualTokens / reservedTokens;
            _reservationEfficiency.Record(efficiency);
        }
    }

    // Observable gauge callbacks
    private double GetTokenUsagePercentage()
    {
        var stats = _rateGate.GetUsageStats();
        return stats.UsagePercentage;
    }

    private int GetTokensAvailable()
    {
        var stats = _rateGate.GetUsageStats();
        return (int)stats.AvailableCapacity;
    }

    private int GetActiveReservationsCount()
    {
        var stats = _rateGate.GetUsageStats();
        return stats.ActiveReservationsCount;
    }

    private int GetWaitingRequestsCount()
    {
        var stats = _rateGate.GetUsageStats();
        return stats.WaitingRequestsCount;
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}
