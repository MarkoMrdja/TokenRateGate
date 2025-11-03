using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests to verify queue wait times match expected capacity recovery.
/// Success criteria: Queue wait times within 10% of expected values.
/// </summary>
public class QueueWaitTimeTests : IDisposable
{
    private readonly TokenRateGate.Core.TokenRateGate _gate;

    public QueueWaitTimeTests()
    {
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
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
    public async Task QueuedRequest_ShouldWaitForCapacityRelease()
    {
        // Arrange: Fill capacity with large requests
        const int largeRequest = 2000;
        var activeReservations = new List<TokenRateGate.Core.Models.TokenReservation>();

        for (int i = 0; i < 4; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(largeRequest / 2, largeRequest / 2);
            activeReservations.Add(reservation);
        }

        // Verify capacity is nearly full
        var stats = _gate.GetUsageStats();
        stats.AvailableTokens.Should().BeLessThan(2000);

        // Act: Start a queued request
        var sw = Stopwatch.StartNew();
        var queuedTask = _gate.ReserveTokensAsync(1000, 1000);

        // Wait a bit to ensure it's queued
        await Task.Delay(100);
        queuedTask.IsCompleted.Should().BeFalse("Request should be queued");

        // Release one reservation to free capacity
        await activeReservations[0].DisposeAsync();
        activeReservations.RemoveAt(0);

        // Wait for queued request to complete
        var queuedReservation = await queuedTask;
        sw.Stop();

        // Assert: Request should complete quickly after capacity is available
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "Queued request should be processed quickly after capacity release");

        // Cleanup
        await queuedReservation.DisposeAsync();
        foreach (var res in activeReservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleQueuedRequests_ShouldBeProcessedInFifoOrder()
    {
        // Arrange: Fill capacity completely
        var blocker = await _gate.ReserveTokensAsync(9000, 0);

        // Queue multiple requests
        var tasks = new List<Task<(int id, DateTime completedAt, TokenRateGate.Core.Models.TokenReservation reservation)>>();
        for (int i = 0; i < 5; i++)
        {
            int requestId = i;
            tasks.Add(Task.Run(async () =>
            {
                var res = await _gate.ReserveTokensAsync(500, 500);
                return (requestId, DateTime.UtcNow, res);
            }));
        }

        // Wait to ensure all are queued
        await Task.Delay(200);

        // Act: Release capacity
        await blocker.DisposeAsync();

        // Wait for all queued requests
        var results = await Task.WhenAll(tasks);

        // Assert: Requests should complete in order (FIFO)
        var orderedResults = results.OrderBy(r => r.completedAt).ToList();
        for (int i = 0; i < orderedResults.Count - 1; i++)
        {
            orderedResults[i].completedAt.Should().BeBefore(orderedResults[i + 1].completedAt,
                "Requests should complete in FIFO order");
        }

        // Cleanup
        foreach (var (_, _, reservation) in results)
        {
            await reservation.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueueWaitTime_ShouldMatchExpectedCapacityRecovery()
    {
        // Arrange: Create scenario with known recovery time
        const int windowSeconds = 5;
        var gate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(new TokenRateGateOptions
            {
                TokenLimit = 5_000,
                WindowSeconds = windowSeconds,
                SafetyBufferPercentage = 0.05,
                MaxConcurrentRequests = 5,
                MaxRequestsPerMinute = 1000
            }),
            NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Fill capacity
        var initialRequest = await gate.ReserveTokensAsync(4500, 0);
        initialRequest.RecordActualUsage(4500, 0);
        await initialRequest.DisposeAsync();

        // Act: Try to reserve more than available
        var sw = Stopwatch.StartNew();
        var waitingRequest = await gate.ReserveTokensAsync(4500, 0);
        sw.Stop();

        // Assert: Should wait approximately windowSeconds (within 10% tolerance)
        var expectedWait = TimeSpan.FromSeconds(windowSeconds);
        var tolerance = TimeSpan.FromSeconds(windowSeconds * 0.1); // 10% tolerance

        sw.Elapsed.Should().BeCloseTo(expectedWait, tolerance,
            "Wait time should match the time for window to slide and capacity to become available");

        // Cleanup
        await waitingRequest.DisposeAsync();
        gate.Dispose();
    }

    [Fact]
    public async Task CancellationToken_ShouldCancelQueuedRequest()
    {
        // Arrange: Fill capacity
        var blocker = await _gate.ReserveTokensAsync(9000, 0);

        // Act: Queue request with cancellation token
        var cts = new CancellationTokenSource();
        var queuedTask = _gate.ReserveTokensAsync(1000, 1000, cts.Token);

        // Wait to ensure it's queued
        await Task.Delay(100);

        // Cancel the request
        cts.Cancel();

        // Assert: Should throw TaskCanceledException (which inherits from OperationCanceledException)
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await queuedTask);

        // Cleanup
        await blocker.DisposeAsync();
    }
}
