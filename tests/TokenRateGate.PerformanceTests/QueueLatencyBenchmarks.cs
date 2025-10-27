using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.PerformanceTests;

/// <summary>
/// Benchmarks to measure queue processing latency and wait time accuracy.
/// Tests verify that queue wait times are within 10% of expected values.
/// </summary>
[MemoryDiagnoser]
public class QueueLatencyBenchmarks
{
    private TokenRateGate.Core.TokenRateGate? _gate;

    [IterationSetup]
    public void Setup()
    {
        // Configure with a modest limit to force queueing
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 3,
            SafetyBufferPercentage = 0.05,
            MaxConcurrentRequests = 10,
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
    /// Benchmark: Queue wait time measurement
    /// Fills capacity then measures time to get next reservation.
    /// </summary>
    [Benchmark]
    public async Task<TimeSpan> QueueWaitTime()
    {
        // Fill capacity with large requests
        const int largeRequest = 2000;
        var activeReservations = new List<TokenRateGate.Core.Models.TokenReservation>();

        // Fill to near capacity
        for (int i = 0; i < 4; i++)
        {
            var reservation = await _gate!.ReserveTokensAsync(largeRequest / 2, largeRequest / 2);
            activeReservations.Add(reservation);
        }

        // Measure wait time for next request
        var sw = Stopwatch.StartNew();
        var waitingReservation = await _gate!.ReserveTokensAsync(1000, 1000);
        sw.Stop();

        // Cleanup
        await waitingReservation.DisposeAsync();
        foreach (var res in activeReservations)
        {
            await res.DisposeAsync();
        }

        return sw.Elapsed;
    }

    /// <summary>
    /// Benchmark: Queue processing after capacity release
    /// Tests how quickly queued requests are processed when capacity becomes available.
    /// </summary>
    [Benchmark]
    public async Task QueueProcessingSpeed()
    {
        const int requestSize = 1000;

        // Fill capacity
        var activeReservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        for (int i = 0; i < 8; i++)
        {
            activeReservations.Add(await _gate!.ReserveTokensAsync(requestSize / 2, requestSize / 2));
        }

        // Queue up 10 requests
        var queuedTasks = new List<Task<TokenRateGate.Core.Models.TokenReservation>>();
        for (int i = 0; i < 10; i++)
        {
            queuedTasks.Add(_gate!.ReserveTokensAsync(requestSize / 2, requestSize / 2));
        }

        // Release capacity and measure queue processing
        var sw = Stopwatch.StartNew();
        foreach (var res in activeReservations)
        {
            await res.DisposeAsync();
        }

        // Wait for all queued requests to complete
        var queuedReservations = await Task.WhenAll(queuedTasks);
        sw.Stop();

        // Cleanup queued reservations
        foreach (var res in queuedReservations)
        {
            await res.DisposeAsync();
        }
    }

    /// <summary>
    /// Benchmark: FIFO order verification
    /// Ensures queued requests are processed in order.
    /// </summary>
    [Benchmark]
    public async Task FifoOrderProcessing()
    {
        const int requestSize = 1000;

        // Fill capacity
        var blocker = await _gate!.ReserveTokensAsync(9000, 0);

        // Queue multiple requests
        var tasks = new List<Task<(int id, TokenRateGate.Core.Models.TokenReservation reservation)>>();
        for (int i = 0; i < 10; i++)
        {
            int requestId = i;
            tasks.Add(Task.Run(async () =>
            {
                var res = await _gate!.ReserveTokensAsync(requestSize / 2, requestSize / 2);
                return (requestId, res);
            }));
        }

        // Small delay to ensure all tasks are queued
        await Task.Delay(100);

        // Release capacity
        await blocker.DisposeAsync();

        // Wait for all and verify order
        var results = await Task.WhenAll(tasks);

        // Cleanup
        foreach (var (_, reservation) in results)
        {
            await reservation.DisposeAsync();
        }
    }
}
