using Microsoft.Extensions.Logging;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Models;

namespace TokenRateGate.StressTests;

/// <summary>
/// Tests for deadlock detection and edge case scenarios
/// </summary>
public class DeadlockAndEdgeCaseTests
{
    private readonly ILoggerFactory _loggerFactory;

    public DeadlockAndEdgeCaseTests()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });
    }

    [Fact]
    public async Task SafetyTimer_ProcessesQueuedRequests_AfterWindowExpiration()
    {
        // Arrange - Use a very short window for predictable expiration timing
        var options = new TokenRateGateOptions
        {
            TokenLimit = 1_000,
            WindowSeconds = 1, // 1 second window
            MaxConcurrentRequests = 10,
            SafetyBufferPercentage = 0.0,
            MaxWaitTime = TimeSpan.FromSeconds(10) // Generous timeout
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act - Fill capacity completely
        var initialReservation = await gate.ReserveTokensAsync(1000, 0);
        initialReservation.RecordActualUsage(1000, 0);
        await initialReservation.DisposeAsync();

        // Start ONE small request that will queue (since 1000 tokens are in the window)
        var queuedTask = Task.Run(async () =>
        {
            var res = await gate.ReserveTokensAsync(100, 0);
            await using var _ = res;
            res.RecordActualUsage(100, 0);
        });

        // Give it time to queue
        await Task.Delay(200);
        var statsWhileQueued = gate.GetUsageStats();
        Assert.True(statsWhileQueued.WaitingRequestsCount > 0, "Request should be queued");

        // Wait for window to expire (1s) + safety timer (100ms intervals) + margin
        // Total: ~1.5 seconds should be plenty
        var timeout = Task.Delay(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(queuedTask, timeout);

        // Assert - Request should complete via safety timer
        Assert.True(completed == queuedTask, "Safety timer should process queued request after window expiration");
        Assert.False(queuedTask.IsFaulted, "Request should not fault");
    }

    [Fact]
    public async Task CancellationDuringQueue_ShouldReleaseProperlyWithoutDeadlock()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 2_000,
            WindowSeconds = 10,
            MaxConcurrentRequests = 20,
            SafetyBufferPercentage = 0.0,
            MaxWaitTime = TimeSpan.FromSeconds(30)
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act - Fill capacity
        var initialReservations = new List<TokenReservation>();
        for (int i = 0; i < 5; i++)
        {
            initialReservations.Add(await gate.ReserveTokensAsync(400, 0));
        }

        // Start requests with cancellation
        var cts = new CancellationTokenSource();
        var cancelledTasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            cancelledTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var res = await gate.ReserveTokensAsync(500, 0, cts.Token);
                    await using var _ = res;
                    res.RecordActualUsage(500, 0);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }));
        }

        await Task.Delay(200);

        // Cancel while requests are queued
        cts.Cancel();
        await Task.WhenAll(cancelledTasks);

        // Release capacity
        foreach (var res in initialReservations)
        {
            res.RecordActualUsage(400, 0);
            await res.DisposeAsync();
        }

        // Assert - New requests should work (no deadlock from cancelled requests)
        var newReservation = await gate.ReserveTokensAsync(500, 0);
        await using var _ = newReservation;
        newReservation.RecordActualUsage(500, 0);

        Assert.True(true, "System recovered from cancelled requests");
    }

    [Fact]
    public async Task ExtremelyLargeRequest_ExceedingTotalLimit_ShouldTimeout()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 10,
            MaxConcurrentRequests = 10,
            SafetyBufferPercentage = 0.0,
            MaxWaitTime = TimeSpan.FromSeconds(2)
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act & Assert - Request larger than total capacity should timeout
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var reservation = await gate.ReserveTokensAsync(15_000, 0);
            await reservation.DisposeAsync();
        });
    }

    [Fact]
    public async Task RapidDisposeAfterReservation_ShouldNotCauseRaceCondition()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,
            WindowSeconds = 30,
            MaxConcurrentRequests = 100,
            SafetyBufferPercentage = 0.1
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act - Rapidly create and dispose reservations
        var tasks = new List<Task>();

        for (int i = 0; i < 1000; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var reservation = await gate.ReserveTokensAsync(50, 50);
                // Immediate dispose (no recording)
                await reservation.DisposeAsync();
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - Should complete without race conditions
        var stats = gate.GetUsageStats();
        Assert.True(stats.CurrentUsage >= 0, "Negative token usage indicates race condition");
        Assert.Equal(0, stats.ActiveReservationsCount);
    }

    [Fact]
    public async Task ConcurrentDisposal_MultipleCalls_ShouldBeIdempotent()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 10,
            MaxConcurrentRequests = 10 // Important: Ensures semaphore is used
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act - Test concurrent disposal with semaphore (critical for TokenRateGate-85)
        var reservation = await gate.ReserveTokensAsync(100, 100);
        reservation.RecordActualUsage(100, 100);

        // Multiple concurrent disposals should be idempotent and not throw
        // Before fix: Would throw SemaphoreFullException on concurrent Release() calls
        var disposalTasks = new List<Task>();
        for (int i = 0; i < 20; i++) // Increased to 20 for more aggressive testing
        {
            disposalTasks.Add(Task.Run(async () =>
            {
                await reservation.DisposeAsync();
            }));
        }

        // Assert - Should complete without any exceptions
        // The fix ensures only one thread actually disposes, preventing semaphore corruption
        await Task.WhenAll(disposalTasks);

        // Verify state is consistent after concurrent disposal
        var stats = gate.GetUsageStats();
        Assert.True(stats.CurrentUsage >= 0, "Negative usage indicates race condition");
        Assert.Equal(0, stats.ActiveReservationsCount); // Should have exactly 0 active reservations

        // Verify we can still make new reservations (semaphore wasn't corrupted)
        var newReservation = await gate.ReserveTokensAsync(100, 100);
        Assert.NotNull(newReservation);
        await newReservation.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentRecordActualUsage_MultipleCalls_ShouldBeIdempotent()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 10,
            MaxConcurrentRequests = 10
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act - Test concurrent RecordActualUsage calls (critical for TokenRateGate-86)
        var reservation = await gate.ReserveTokensAsync(100, 100);

        // Multiple concurrent RecordActualUsage calls should be idempotent
        // Before fix: Would corrupt statistics by recording usage multiple times
        var recordTasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            int iteration = i;
            recordTasks.Add(Task.Run(() =>
            {
                // Each thread tries to record different values - only first should win
                reservation.RecordActualUsage(100 + iteration, 100 + iteration);
            }));
        }

        // Should complete without exceptions
        await Task.WhenAll(recordTasks);

        // Assert - Only one call should have been recorded (but we don't know which thread won the race)
        Assert.NotNull(reservation.ActualTokensUsed);
        // The value should be in the range [200, 219] (from iterations 0-19: 100+i, 100+i)
        Assert.InRange(reservation.ActualTokensUsed.Value, 200, 219);

        var recordedUsage = reservation.ActualTokensUsed.Value;
        await reservation.DisposeAsync();

        // Verify statistics are consistent (not corrupted by multiple recordings)
        var stats = gate.GetUsageStats();
        Assert.Equal(recordedUsage, stats.CurrentUsage); // Should match whatever value was recorded, not sum of all attempts
    }

    [Fact]
    public async Task SimultaneousWindowExpiration_ShouldNotCauseInconsistency()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 2, // Short window to trigger frequent cleanup
            MaxConcurrentRequests = 50,
            SafetyBufferPercentage = 0.0
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Act - Create requests that will expire while new ones arrive
        var tasks = new List<Task>();

        for (int batch = 0; batch < 5; batch++)
        {
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var reservation = await gate.ReserveTokensAsync(100, 100);
                        await using var _ = reservation;
                        await Task.Delay(50);
                        reservation.RecordActualUsage(100, 100);
                    }
                    catch
                    {
                        // Ignore
                    }
                }));
            }

            await Task.Delay(TimeSpan.FromSeconds(1)); // Overlap with window expiration
        }

        await Task.WhenAll(tasks);

        // Assert - Consistency after window expirations
        var stats = gate.GetUsageStats();
        Assert.True(stats.CurrentUsage >= 0);
        Assert.True(stats.AvailableTokens <= options.TokenLimit);
    }
}
