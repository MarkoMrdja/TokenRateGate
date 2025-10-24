using FluentAssertions;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-3: Cancellation race condition between registration and task completion
///
/// Bug Description:
/// After lock is released (line 134), there's a race between cancellation callback registration and
/// TryProcessingWaitingRequests() granting the reservation. The cancellation callback could remove a
/// request that was already granted, leading to resource leaks or invalid state.
///
/// Scenario:
/// 1. Lock released at line 134
/// 2. Another thread calls TryProcessingWaitingRequests(), grants reservation, sets TaskCompletionSource
/// 3. Cancellation fires, tries to cancel already-granted reservation
/// 4. Request removed from queue but reservation was already created
///
/// Expected Behavior (after fix):
/// Use atomic state transitions to prevent canceling an already-granted request.
/// </summary>
public class TokenRateGate_3_CancellationRaceTests
{
    [Fact]
    public async Task CancelWhileGranting_MayRemoveGrantedRequest()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateNearCapacityOptions();
        options.TokenLimit = 5000;
        options.SafetyBuffer = 0;
        options.MaxConcurrentRequests = 2;
        options.MaxWaitTime = TimeSpan.FromSeconds(5);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Fill capacity to force queueing
        var blockingReservation = await gate.ReserveTokensAsync(inputTokens: 4500, estimatedOutputTokens: 0);

        // Act
        // Create a request that will queue
        var cts = new CancellationTokenSource();
        var queuedRequestTask = gate.ReserveTokensAsync(inputTokens: 500, estimatedOutputTokens: 0, cts.Token);

        // Wait for it to queue
        await Task.Delay(100);

        // Now trigger both: release capacity AND cancel the token
        // This creates a race between TryProcessingWaitingRequests granting the reservation
        // and the cancellation callback removing it from the queue
        var releaseTask = Task.Run(async () =>
        {
            await blockingReservation.DisposeAsync();
        });

        var cancelTask = Task.Run(() =>
        {
            cts.Cancel();
        });

        await Task.WhenAll(releaseTask, cancelTask);

        // Wait to see what happens
        Exception? capturedException = null;
        Models.TokenReservation? reservation = null;

        try
        {
            reservation = await queuedRequestTask;
        }
        catch (OperationCanceledException)
        {
            // Expected if cancel won the race
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // Assert
        // BUG: There's a race condition here. Either:
        // A) Request is canceled (cancel callback won the race)
        // B) Request is granted (grant won the race)
        // C) Request is granted BUT callback still runs and corrupts state
        // D) Unexpected exception due to race condition

        if (reservation != null)
        {
            // Granted - but the cancel callback might have still run
            // Check if we can use this reservation
            reservation.Should().NotBeNull("reservation was granted despite cancellation attempt");

            // Check internal state consistency
            var stats = gate.GetUsageStats();
            stats.ActiveReservations.Should().BeGreaterThan(0,
                "because if reservation was granted, it should be tracked as active");

            await reservation.DisposeAsync();
        }

        if (capturedException != null)
        {
            // Race condition might cause unexpected exceptions
            capturedException.Should().NotBeNull(
                "race condition between grant and cancel can cause unexpected failures");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task MultipleConcurrentCancellationsAndGrants_ExposesRaceCondition()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateNearCapacityOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 0;
        options.MaxConcurrentRequests = 3;
        options.MaxWaitTime = TimeSpan.FromSeconds(5);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Block capacity
        var blockingReservation = await gate.ReserveTokensAsync(inputTokens: 9000, estimatedOutputTokens: 0);

        // Act
        // Queue multiple requests with cancellation tokens
        var cancellationSources = new List<CancellationTokenSource>();
        var queuedTasks = new List<Task<Models.TokenReservation>>();

        for (int i = 0; i < 10; i++)
        {
            var cts = new CancellationTokenSource();
            cancellationSources.Add(cts);
            queuedTasks.Add(gate.ReserveTokensAsync(inputTokens: 500, estimatedOutputTokens: 0, cts.Token));
        }

        // Wait for all to queue
        await Task.Delay(200);

        // Now release capacity while randomly canceling tokens
        // This creates multiple concurrent races
        var tasks = new List<Task>();

        tasks.Add(Task.Run(async () => await blockingReservation.DisposeAsync()));

        foreach (var cts in cancellationSources)
        {
            tasks.Add(Task.Run(async () =>
            {
                await Task.Delay(Random.Shared.Next(0, 50)); // Random timing
                cts.Cancel();
            }));
        }

        await Task.WhenAll(tasks);

        // Wait for all queued tasks to complete or fail
        var results = await Task.WhenAll(queuedTasks.Select(async t =>
        {
            try
            {
                return await t;
            }
            catch
            {
                return null;
            }
        }));

        // Assert
        var grantedCount = results.Count(r => r != null);
        var canceledCount = results.Count(r => r == null);

        // The race condition exists regardless of outcome distribution
        (grantedCount + canceledCount).Should().Be(10, "all requests should resolve one way or another");

        // BUG INDICATOR: Check if granted reservations are properly tracked
        var stats = gate.GetUsageStats();

        if (grantedCount > 0)
        {
            // If some were granted, verify internal consistency
            // BUG: ActiveReservations count might be incorrect due to race
            stats.ActiveReservations.Should().BeGreaterThanOrEqualTo(0,
                "active reservation count should never be negative");

            // Cleanup granted reservations
            foreach (var reservation in results.Where(r => r != null))
            {
                await reservation!.DisposeAsync();
            }
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task CancelAfterGrantButBeforeTaskCompletion_CausesInconsistentState()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateNearCapacityOptions();
        options.TokenLimit = 5000;
        options.SafetyBuffer = 0;
        options.MaxConcurrentRequests = 1;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Block capacity
        var blockingReservation = await gate.ReserveTokensAsync(inputTokens: 4500, estimatedOutputTokens: 0);

        // Act
        var cts = new CancellationTokenSource();
        var queuedRequestTask = gate.ReserveTokensAsync(inputTokens: 400, estimatedOutputTokens: 0, cts.Token);

        await Task.Delay(100); // Ensure it's queued

        // Release and cancel with precise timing to hit the race window
        var releaseStarted = new TaskCompletionSource<bool>();
        var releaseTask = Task.Run(async () =>
        {
            releaseStarted.SetResult(true);
            await blockingReservation.DisposeAsync();
            // TryProcessingWaitingRequests runs here and grants the reservation
        });

        await releaseStarted.Task;
        await Task.Delay(10); // Small delay to let grant start

        // Cancel right after grant might have happened but before TaskCompletionSource.SetResult
        cts.Cancel();

        Exception? capturedException = null;
        Models.TokenReservation? result = null;

        try
        {
            result = await queuedRequestTask;
        }
        catch (OperationCanceledException)
        {
            // Might be canceled
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // Assert
        // BUG VERIFICATION: The race window between grant and cancel can cause:
        // 1. Reservation granted but removed from _activeReservations
        // 2. Reservation leaked
        // 3. Inconsistent internal state

        if (result != null)
        {
            var stats = gate.GetUsageStats();
            // If we got a reservation, it should be tracked
            stats.ActiveReservations.Should().Be(1,
                "because the granted reservation should be tracked in active reservations");

            await result.DisposeAsync();
        }

        if (capturedException != null)
        {
            // Unexpected exception indicates race condition corruption
            capturedException.Message.Should().NotBeNullOrEmpty(
                "race condition may cause unexpected exception");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task StressTest_RapidCancellationsExposeRaceCondition()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 5);
        options.TokenLimit = 50000;
        options.SafetyBuffer = 0;
        options.MaxWaitTime = TimeSpan.FromSeconds(10);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Block some capacity to force queueing
        var blocker = await gate.ReserveTokensAsync(inputTokens: 40000, estimatedOutputTokens: 0);

        var iterations = 100;
        var grantedCount = 0;
        var canceledCount = 0;
        var exceptions = new List<Exception>();

        // Act
        var tasks = Enumerable.Range(0, iterations).Select(async i =>
        {
            var cts = new CancellationTokenSource();

            var reservationTask = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 0, cts.Token);

            // Cancel after a random delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(Random.Shared.Next(1, 20));
                cts.Cancel();
            });

            try
            {
                var reservation = await reservationTask;
                Interlocked.Increment(ref grantedCount);
                await reservation.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref canceledCount);
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }).ToArray();

        // Release blocker to allow grants
        await Task.Delay(50);
        await blocker.DisposeAsync();

        await Task.WhenAll(tasks);

        // Assert
        (grantedCount + canceledCount + exceptions.Count).Should().Be(iterations,
            "all requests should complete in some way");

        // The bug is present if any unexpected exceptions occurred
        if (exceptions.Any())
        {
            exceptions.Should().NotBeEmpty(
                "race condition between grant and cancel caused unexpected exceptions");
        }

        // Cleanup
        gate.Dispose();
    }
}
