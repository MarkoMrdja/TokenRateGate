using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests to verify concurrent request handling matches MaxConcurrentRequests.
/// Success criteria: System respects concurrency limits and handles concurrent load correctly.
/// </summary>
public class ConcurrentRequestTests : IDisposable
{
    private readonly TokenRateGate.Core.TokenRateGate _gate;

    public ConcurrentRequestTests()
    {
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 10,
            SafetyBufferPercentage = 0.05,
            MaxConcurrentRequests = 10,
            MaxRequestsPerMinute = 1000,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);
    }

    public void Dispose()
    {
        _gate?.Dispose();
    }

    [Fact]
    public async Task ConcurrentReservations_ShouldRespectMaxConcurrentRequests()
    {
        // Arrange: Set max concurrent to 5
        var gate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(new TokenRateGateOptions
            {
                TokenLimit = 100_000,
                WindowSeconds = 10,
                SafetyBufferPercentage = 0.05,
                MaxConcurrentRequests = 5, // Limit to 5 concurrent
                MaxRequestsPerMinute = 1000
            }),
            NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Try to make more concurrent requests than allowed
        int maxObservedConcurrency = 0;
        int currentConcurrency = 0;
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                await using var reservation = await gate.ReserveTokensAsync(1000, 1000);

                lock (lockObj)
                {
                    currentConcurrency++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, currentConcurrency);
                }

                await Task.Delay(100); // Hold reservation briefly

                lock (lockObj)
                {
                    currentConcurrency--;
                }

                reservation.RecordActualUsage(1000, 1000);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert: Max observed concurrency should not exceed limit
        maxObservedConcurrency.Should().BeLessThanOrEqualTo(5,
            "System should respect MaxConcurrentRequests limit");

        gate.Dispose();
    }

    [Fact]
    public async Task HighConcurrency_ShouldHandleCorrectly()
    {
        // Arrange: 50 concurrent requests
        const int concurrentCount = 50;
        const int tokensPerRequest = 500;

        // Act: Make many concurrent requests
        long totalProcessed = 0;
        var tasks = Enumerable.Range(0, concurrentCount)
            .Select(_ => Task.Run(async () =>
            {
                await using var reservation = await _gate.ReserveTokensAsync(
                    tokensPerRequest / 2,
                    tokensPerRequest / 2);

                await Task.Delay(10); // Brief work

                reservation.RecordActualUsage(
                    tokensPerRequest / 2,
                    tokensPerRequest / 2);

                Interlocked.Add(ref totalProcessed, tokensPerRequest);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert: All requests should complete successfully
        totalProcessed.Should().Be(concurrentCount * tokensPerRequest,
            "All concurrent requests should be processed");
    }

    [Fact]
    public async Task MixedConcurrentOperations_ShouldNotDeadlock()
    {
        // Arrange: Mix of reservations, releases, and stats queries
        const int operationsPerType = 20;
        var completedOps = 0;

        // Act: Run mixed operations concurrently
        var reservationTasks = Enumerable.Range(0, operationsPerType)
            .Select(_ => Task.Run(async () =>
            {
                await using var res = await _gate.ReserveTokensAsync(500, 500);
                await Task.Delay(Random.Shared.Next(1, 20));
                res.RecordActualUsage(500, 500);
                Interlocked.Increment(ref completedOps);
            }));

        var statsTasks = Enumerable.Range(0, operationsPerType)
            .Select(_ => Task.Run(async () =>
            {
                await Task.Delay(Random.Shared.Next(1, 20));
                var stats = _gate.GetUsageStats();
                _ = (int)stats.CurrentUsage;
                Interlocked.Increment(ref completedOps);
            }));

        var allTasks = reservationTasks.Concat(statsTasks).ToArray();

        // Should complete without deadlock
        var allTasksCompletion = Task.WhenAll(allTasks);
        var completedTask = await Task.WhenAny(
            allTasksCompletion,
            Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert: All operations should complete (no deadlock)
        completedTask.Should().BeSameAs(allTasksCompletion,
            "All operations should complete without deadlock");

        completedOps.Should().Be(operationsPerType * 2,
            "All operations should complete successfully");
    }

    [Fact]
    public async Task ConcurrentQueueing_ShouldProcessAllRequests()
    {
        // Arrange: Fill capacity and queue many requests
        const int queuedCount = 30;

        // Fill capacity
        var blocker = await _gate.ReserveTokensAsync(95_000, 0);

        // Act: Queue many requests concurrently
        long processedCount = 0;
        var tasks = Enumerable.Range(0, queuedCount)
            .Select(_ => Task.Run(async () =>
            {
                await using var res = await _gate.ReserveTokensAsync(100, 100);
                res.RecordActualUsage(100, 100);
                Interlocked.Increment(ref processedCount);
            }))
            .ToArray();

        // Wait a bit to ensure all are queued
        await Task.Delay(200);

        // Release capacity
        await blocker.DisposeAsync();

        // Wait for all queued requests
        await Task.WhenAll(tasks);

        // Assert: All queued requests should be processed
        processedCount.Should().Be(queuedCount,
            "All queued requests should eventually be processed");
    }

    [Fact]
    public async Task ThreadSafety_ShouldMaintainDataIntegrity()
    {
        // Arrange: Many concurrent operations on shared gate instance
        const int threadsCount = 20;
        const int operationsPerThread = 50;

        // Act: Hammer the gate with concurrent operations
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, threadsCount)
            .Select(threadId => Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < operationsPerThread; i++)
                    {
                        await using var reservation = await _gate.ReserveTokensAsync(100, 100);

                        // Query stats while holding reservation
                        var stats = _gate.GetUsageStats();
                        _ = stats.AvailableCapacity;

                        reservation.RecordActualUsage(100, 100);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert: No exceptions should occur
        exceptions.Should().BeEmpty(
            "Thread-safe operations should not throw exceptions");

        // Final stats should be consistent
        var finalStats = _gate.GetUsageStats();
        finalStats.CurrentUsage.Should().BeGreaterThanOrEqualTo(0,
            "Stats should maintain data integrity");
    }

    [Fact]
    public async Task ConcurrentCancellations_ShouldHandleGracefully()
    {
        // Arrange: Queue requests and cancel some
        var blocker = await _gate.ReserveTokensAsync(95_000, 0);

        // Act: Queue requests with cancellation tokens
        int successfulReservations = 0;
        int cancelledReservations = 0;

        var tasks = new List<Task>();
        var cancellationSources = new List<CancellationTokenSource>();

        for (int i = 0; i < 20; i++)
        {
            var cts = new CancellationTokenSource();
            cancellationSources.Add(cts);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await using var res = await _gate.ReserveTokensAsync(100, 100, cts.Token);
                    Interlocked.Increment(ref successfulReservations);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelledReservations);
                }
            }));
        }

        // Wait for all to queue
        await Task.Delay(200);

        // Cancel half of them
        for (int i = 0; i < cancellationSources.Count / 2; i++)
        {
            cancellationSources[i].Cancel();
        }

        // Release capacity
        await blocker.DisposeAsync();

        await Task.WhenAll(tasks);

        // Assert: Should have both successful and cancelled requests
        cancelledReservations.Should().BeGreaterThan(0,
            "Some requests should be cancelled");

        successfulReservations.Should().BeGreaterThan(0,
            "Some requests should succeed");

        (successfulReservations + cancelledReservations).Should().Be(20,
            "All requests should either succeed or be cancelled");

        // Cleanup
        foreach (var cts in cancellationSources)
        {
            cts.Dispose();
        }
    }
}
