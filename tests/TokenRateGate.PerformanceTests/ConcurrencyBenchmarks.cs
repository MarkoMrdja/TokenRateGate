using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.PerformanceTests;

/// <summary>
/// Benchmarks to measure lock contention and concurrency handling under high load.
/// Tests validate that the system can handle 10+ concurrent requests efficiently.
/// </summary>
[MemoryDiagnoser]
public class ConcurrencyBenchmarks
{
    private TokenRateGate.Core.TokenRateGate? _gate;

    [Params(5, 10, 20, 50)]
    public int ConcurrentRequests { get; set; }

    [IterationSetup]
    public void Setup()
    {
        // Configure for high concurrency testing
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 500_000,
            WindowSeconds = 5,
            SafetyBufferPercentage = 0.05,
            MaxConcurrentRequests = ConcurrentRequests * 2, // Allow high concurrency
            MaxRequestsPerMinute = 10000,
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
    /// Benchmark: High concurrency reservation test
    /// Measures lock contention with many simultaneous reservation attempts.
    /// </summary>
    [Benchmark]
    public async Task HighConcurrencyReservations()
    {
        const int requestsPerThread = 50;
        const int tokensPerRequest = 1000;

        var tasks = Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < requestsPerThread; i++)
                {
                    await using var reservation = await _gate!.ReserveTokensAsync(
                        tokensPerRequest / 2,
                        tokensPerRequest / 2);

                    await Task.Yield(); // Simulate minimal work

                    reservation.RecordActualUsage(
                        tokensPerRequest / 2,
                        (int)(tokensPerRequest / 2 * 0.9));
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Benchmark: Mixed read/write operations
    /// Tests contention between reservations, releases, and stats queries.
    /// </summary>
    [Benchmark]
    public async Task MixedReadWriteOperations()
    {
        const int opsPerThread = 100;
        const int tokensPerRequest = 500;

        var tasks = Enumerable.Range(0, ConcurrentRequests)
            .Select(threadId => Task.Run(async () =>
            {
                for (int i = 0; i < opsPerThread; i++)
                {
                    if (i % 10 == 0)
                    {
                        // Every 10th operation is a stats query
                        var stats = _gate!.GetUsageStats();
                        _ = stats.CurrentUsage;
                    }
                    else
                    {
                        // Normal reservation
                        await using var reservation = await _gate!.ReserveTokensAsync(
                            tokensPerRequest / 2,
                            tokensPerRequest / 2);

                        await Task.Yield();

                        reservation.RecordActualUsage(
                            tokensPerRequest / 2,
                            tokensPerRequest / 2);
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Benchmark: Rapid acquire/release cycles
    /// Tests lock overhead with very quick reservation lifecycles.
    /// </summary>
    [Benchmark]
    public async Task RapidAcquireReleaseCycles()
    {
        const int cyclesPerThread = 200;
        const int tokensPerRequest = 100;

        var tasks = Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < cyclesPerThread; i++)
                {
                    await using var reservation = await _gate!.ReserveTokensAsync(
                        tokensPerRequest / 2,
                        tokensPerRequest / 2);

                    // Immediately release (no work simulation)
                    reservation.RecordActualUsage(
                        tokensPerRequest / 2,
                        tokensPerRequest / 2);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Benchmark: Contention hotspot test
    /// All threads compete for reservations at the same time.
    /// </summary>
    [Benchmark]
    public async Task ContentionHotspot()
    {
        const int iterations = 10;
        const int tokensPerRequest = 1000;

        for (int iter = 0; iter < iterations; iter++)
        {
            // Create a barrier - all threads start simultaneously
            var barrier = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, ConcurrentRequests)
                .Select(_ => Task.Run(async () =>
                {
                    await barrier.Task; // Wait for signal

                    await using var reservation = await _gate!.ReserveTokensAsync(
                        tokensPerRequest / 2,
                        tokensPerRequest / 2);

                    await Task.Delay(5); // Brief simulated work

                    reservation.RecordActualUsage(
                        tokensPerRequest / 2,
                        tokensPerRequest / 2);
                }))
                .ToArray();

            // Release all threads simultaneously
            barrier.SetResult();

            await Task.WhenAll(tasks);
        }
    }
}
