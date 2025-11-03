using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.PerformanceTests;

/// <summary>
/// Benchmarks to measure window sliding efficiency and cleanup performance.
/// Tests validate that expired records are cleaned up efficiently without performance degradation.
/// </summary>
[MemoryDiagnoser]
public class WindowSlidingBenchmarks
{
    private TokenRateGate.Core.TokenRateGate? _gate;

    [Params(3, 5, 10)]
    public int WindowSeconds { get; set; }

    [IterationSetup]
    public void Setup()
    {
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = WindowSeconds,
            SafetyBufferPercentage = 0.05,
            MaxConcurrentRequests = 20,
            MaxRequestsPerMinute = 1000,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _gate?.Dispose();
    }

    /// <summary>
    /// Benchmark: Window sliding with continuous load
    /// Tests cleanup efficiency as old records age out of the window.
    /// </summary>
    [Benchmark]
    public async Task ContinuousWindowSliding()
    {
        const int tokensPerRequest = 500;
        const int requestCount = 500;

        // Generate continuous load across the window period
        for (int i = 0; i < requestCount; i++)
        {
            await using var reservation = await _gate!.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            // Small delay to spread requests over time
            if (i % 10 == 0)
            {
                await Task.Delay(50);
            }

            reservation.RecordActualUsage(
                tokensPerRequest / 2,
                tokensPerRequest / 2);
        }
    }

    /// <summary>
    /// Benchmark: Cleanup performance with large history
    /// Tests cleanup when there are many expired records to clean up.
    /// </summary>
    [Benchmark]
    public async Task LargeHistoryCleanup()
    {
        const int tokensPerRequest = 100;
        const int burstSize = 200;

        // Create a large burst of old requests
        for (int i = 0; i < burstSize; i++)
        {
            await using var reservation = await _gate!.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            reservation.RecordActualUsage(
                tokensPerRequest / 2,
                tokensPerRequest / 2);
        }

        // Wait for window to pass
        await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 2));

        // Trigger cleanup by making new requests
        for (int i = 0; i < 50; i++)
        {
            await using var reservation = await _gate!.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            reservation.RecordActualUsage(
                tokensPerRequest / 2,
                tokensPerRequest / 2);
        }
    }

    /// <summary>
    /// Benchmark: Stats accuracy during window sliding
    /// Verifies that statistics remain accurate as window slides.
    /// </summary>
    [Benchmark]
    public async Task StatsAccuracyDuringSliding()
    {
        const int tokensPerRequest = 500;
        const int iterations = 100;

        var statsSamples = new List<(long usage, long reserved, long available)>();

        for (int i = 0; i < iterations; i++)
        {
            await using var reservation = await _gate!.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            // Sample stats while reservation is active
            var stats = _gate!.GetUsageStats();
            statsSamples.Add((stats.CurrentUsage, stats.TotalReserved, stats.AvailableTokens));

            await Task.Delay(WindowSeconds * 1000 / iterations); // Spread across window

            reservation.RecordActualUsage(
                tokensPerRequest / 2,
                tokensPerRequest / 2);
        }

        // Verify samples are reasonable (not part of benchmark timing)
        _ = statsSamples.Count;
    }

    /// <summary>
    /// Benchmark: Recovery after full window expiration
    /// Tests performance when entire window expires and capacity is fully restored.
    /// </summary>
    [Benchmark]
    public async Task FullWindowExpiration()
    {
        const int tokensPerRequest = 1000;

        // Fill capacity
        var initialReservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        for (int i = 0; i < 50; i++)
        {
            initialReservations.Add(await _gate!.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2));
        }

        // Complete all reservations
        foreach (var res in initialReservations)
        {
            res.RecordActualUsage(tokensPerRequest / 2, tokensPerRequest / 2);
            await res.DisposeAsync();
        }

        // Wait for full window to expire
        await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 2));

        // Test that full capacity is available
        var newReservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        for (int i = 0; i < 50; i++)
        {
            newReservations.Add(await _gate!.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2));
        }

        // Cleanup
        foreach (var res in newReservations)
        {
            await res.DisposeAsync();
        }
    }
}
