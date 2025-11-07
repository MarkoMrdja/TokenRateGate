using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests to verify no deadlocks occur and safety timer functions correctly.
/// Success criteria: No deadlocks, safety timer prevents request starvation.
/// </summary>
public class SafetyTimerTests : IDisposable
{
    private readonly TokenRateGate.Core.TokenRateGate _gate;

    public SafetyTimerTests()
    {
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 50_000,
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
    public async Task NoActiveReservations_WithWaitingRequests_ShouldNotDeadlock()
    {
        // This tests the safety timer scenario:
        // - Waiting requests exist
        // - No active reservations
        // - Capacity should be available

        // Arrange: Fill and then release capacity quickly
        var blocker = await _gate.ReserveTokensAsync(45_000, 0);

        // Act: Queue a request
        var waitingTask = Task.Run(async () =>
        {
            await using var reservation = await _gate.ReserveTokensAsync(1000, 1000);
            return true;
        });

        // Wait to ensure request is queued
        await Task.Delay(100);

        // Release the blocker (this should trigger queue processing)
        await blocker.DisposeAsync();

        // Wait with timeout for the queued request
        var completedTask = await Task.WhenAny(
            waitingTask,
            Task.Delay(TimeSpan.FromSeconds(5)));

        // Assert: Should complete (not deadlock)
        completedTask.Should().Be(waitingTask,
            "Waiting request should be processed after capacity release");

        var result = await waitingTask;
        result.Should().BeTrue("Request should complete successfully");
    }

    [Fact]
    public async Task ComplexQueueingScenario_ShouldNotDeadlock()
    {
        // Arrange: Create complex scenario with multiple phases
        const int tokensPerRequest = 5000;

        // Phase 1: Fill capacity
        var initialReservations = new List<TokenRateGate.Core.Models.TokenReservation>
        {
            await _gate.ReserveTokensAsync(tokensPerRequest, 0),
            await _gate.ReserveTokensAsync(tokensPerRequest, 0),
            await _gate.ReserveTokensAsync(tokensPerRequest, 0),
            await _gate.ReserveTokensAsync(tokensPerRequest, 0)
        };

        // Phase 2: Queue multiple requests
        var queuedTasks = new List<Task<bool>>();
        for (int i = 0; i < 10; i++)
        {
            queuedTasks.Add(Task.Run(async () =>
            {
                await using var res = await _gate.ReserveTokensAsync(1000, 1000);
                res.RecordActualUsage(1000, 1000);
                return true;
            }));
        }

        await Task.Delay(200);

        // Phase 3: Release capacity in stages
        foreach (var res in initialReservations)
        {
            await res.DisposeAsync();
            await Task.Delay(50);
        }

        // Phase 4: All queued requests should complete
        var queuedTasksCompletion = Task.WhenAll(queuedTasks);
        var allCompleted = await Task.WhenAny(
            queuedTasksCompletion,
            Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert: Should not deadlock
        allCompleted.Should().BeSameAs(queuedTasksCompletion,
            "All queued requests should complete without deadlock");

        var results = await Task.WhenAll(queuedTasks);
        results.Should().AllBeEquivalentTo(true,
            "All queued requests should succeed");
    }

    [Fact]
    public async Task RapidReleaseAndQueue_ShouldNotDeadlock()
    {
        // Tests rapid cycling between full and available capacity
        const int iterations = 20;

        for (int i = 0; i < iterations; i++)
        {
            // Fill capacity
            var blocker = await _gate.ReserveTokensAsync(45_000, 0);

            // Queue request
            var queuedTask = _gate.ReserveTokensAsync(1000, 1000);

            // Immediately release
            await blocker.DisposeAsync();

            // Should complete quickly
            var reservation = await queuedTask.WaitAsync(TimeSpan.FromSeconds(2));
            await reservation.DisposeAsync();
        }

        // If we get here without timeout, test passes (no assertion needed)
    }

    [Fact]
    public async Task MultipleWaitingQueues_ShouldAllBeProcessed()
    {
        // Arrange: Create scenario where multiple requests wait at different times
        var blocker = await _gate.ReserveTokensAsync(45_000, 0);

        // Wave 1: Queue first batch
        var wave1 = Enumerable.Range(0, 5)
            .Select(_ => _gate.ReserveTokensAsync(500, 500))
            .ToArray();

        await Task.Delay(100);

        // Wave 2: Queue second batch
        var wave2 = Enumerable.Range(0, 5)
            .Select(_ => _gate.ReserveTokensAsync(500, 500))
            .ToArray();

        await Task.Delay(100);

        // Act: Release capacity
        await blocker.DisposeAsync();

        // Assert: All should complete
        var allTasks = wave1.Concat(wave2).ToArray();
        var allTasksCompletion = Task.WhenAll(allTasks);
        var completedTask = await Task.WhenAny(
            allTasksCompletion,
            Task.Delay(TimeSpan.FromSeconds(10)));

        completedTask.Should().BeSameAs(allTasksCompletion,
            "All waves should be processed without deadlock");

        // Cleanup
        var reservations = await Task.WhenAll(allTasks);
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task SafetyTimer_ShouldRecoverFromStuckState()
    {
        // This test simulates a potential stuck state and verifies recovery
        // The safety timer should help process waiting requests

        // Arrange: Create edge case scenario
        var gate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(new TokenRateGateOptions
            {
                TokenLimit = 10_000,
                WindowSeconds = 5,
                SafetyBufferPercentage = 0.05,
                MaxConcurrentRequests = 3,
                MaxRequestsPerMinute = 100
            }),
            NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Fill near capacity
        var blocker = await gate.ReserveTokensAsync(9000, 0);

        // Queue several requests
        var queuedTasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                await using var res = await gate.ReserveTokensAsync(500, 500);
                return DateTime.UtcNow;
            }))
            .ToArray();

        await Task.Delay(200);

        // Release blocker
        await blocker.DisposeAsync();

        // Act: Wait for queued requests with generous timeout
        // Safety timer should ensure they're processed
        var queuedTasksCompletion = Task.WhenAll(queuedTasks);
        var completed = await Task.WhenAny(
            queuedTasksCompletion,
            Task.Delay(TimeSpan.FromSeconds(15)));

        // Assert: Should complete (safety timer prevents infinite waiting)
        completed.Should().BeSameAs(queuedTasksCompletion,
            "Safety timer should ensure queued requests are eventually processed");

        gate.Dispose();
    }

    [Fact]
    public async Task DisposalDuringOperations_ShouldNotCauseDeadlock()
    {
        // Arrange: Create operations that will be interrupted by disposal
        var gate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(new TokenRateGateOptions
            {
                TokenLimit = 50_000,
                WindowSeconds = 10,
                SafetyBufferPercentage = 0.05,
                MaxConcurrentRequests = 10,
                MaxRequestsPerMinute = 1000
            }),
            NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Start some operations
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await using var res = await gate.ReserveTokensAsync(1000, 1000);
                    await Task.Delay(100);
                    res.RecordActualUsage(1000, 1000);
                }
                catch (ObjectDisposedException)
                {
                    // Expected when gate is disposed
                }
            }))
            .ToArray();

        // Act: Dispose while operations are running
        await Task.Delay(50);
        gate.Dispose();

        // Wait for all tasks to complete
        var tasksCompletion = Task.WhenAll(tasks);
        var allCompleted = await Task.WhenAny(
            tasksCompletion,
            Task.Delay(TimeSpan.FromSeconds(5)));

        // Assert: Should complete without hanging
        allCompleted.Should().BeSameAs(tasksCompletion,
            "Disposal should not cause operations to hang");
    }

    [Fact]
    public async Task LongRunningReservations_ShouldNotBlockQueue()
    {
        // Arrange: Create long-running reservations
        var longRunningTasks = new List<Task>();

        for (int i = 0; i < 3; i++)
        {
            longRunningTasks.Add(Task.Run(async () =>
            {
                await using var res = await _gate.ReserveTokensAsync(5000, 5000);
                await Task.Delay(2000); // Hold for 2 seconds
                res.RecordActualUsage(5000, 5000);
            }));
        }

        await Task.Delay(200);

        // Act: Make quick requests while long ones are running
        var quickTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                await using var res = await _gate.ReserveTokensAsync(500, 500);
                res.RecordActualUsage(500, 500);
                return true;
            }))
            .ToArray();

        // Assert: Quick tasks should complete eventually
        var quickTasksCompletion = Task.WhenAll(quickTasks);
        var quickCompleted = await Task.WhenAny(
            quickTasksCompletion,
            Task.Delay(TimeSpan.FromSeconds(15)));

        quickCompleted.Should().BeSameAs(quickTasksCompletion,
            "Quick requests should be processed alongside long-running ones");

        // Wait for long tasks to complete
        await Task.WhenAll(longRunningTasks);
    }
}
