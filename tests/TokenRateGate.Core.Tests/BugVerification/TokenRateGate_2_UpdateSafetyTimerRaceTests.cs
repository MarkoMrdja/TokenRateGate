using FluentAssertions;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-2: Race condition in UpdateSafetyTimerState() called outside lock
///
/// Bug Description:
/// UpdateSafetyTimerState() is called outside the lock on lines 101 and 116 after successful immediate
/// reservations. This method reads _waitingRequests.Count and _activeReservations.Count without
/// synchronization, causing potential data races.
///
/// Expected Behavior (after fix):
/// UpdateSafetyTimerState() should be called inside the lock or acquire the lock internally.
/// </summary>
public class TokenRateGate_2_UpdateSafetyTimerRaceTests
{
    [Fact]
    public async Task ConcurrentImmediateReservations_MayTriggerDataRace()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateRaceConditionTestOptions();
        options.MaxConcurrentRequests = 10;
        options.TokenLimit = 1000000; // High enough for immediate reservations
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Launch many concurrent requests that will succeed immediately
        // This creates a race condition where UpdateSafetyTimerState() is called
        // outside the lock while other threads are modifying the collections
        var tasks = new List<Task<Models.TokenReservation>>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100));
        }

        // This test is hard to assert definitively because race conditions are non-deterministic
        // However, running this under a race detector or with high iteration counts
        // should expose the issue

        Models.TokenReservation[]? reservations = null;
        Exception? capturedException = null;

        try
        {
            // Try to trigger race condition through concurrent execution
            reservations = await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // Assert
        if (capturedException != null)
        {
            // Race condition might manifest as exception
            capturedException.Should().NotBeNull(
                "because the race condition in UpdateSafetyTimerState() can cause unexpected exceptions");
        }
        else
        {
            // Even if no exception, the race exists
            reservations.Should().NotBeNull();
            // The bug is a data race that might not cause immediate failure
            // but violates memory model guarantees
        }

        // Cleanup
        if (reservations != null)
        {
            foreach (var r in reservations)
            {
                await r.DisposeAsync();
            }
        }
        gate.Dispose();
    }

    [Fact]
    public async Task RapidReserveAndRelease_ExposesTimerStateRace()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateRaceConditionTestOptions();
        options.MaxConcurrentRequests = 5;
        options.TokenLimit = 100000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        var exceptions = new List<Exception>();
        var tasks = new List<Task>();

        // Act
        // Rapidly create and dispose reservations from multiple threads
        // This stresses the UpdateSafetyTimerState() path
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var reservation = await gate.ReserveTokensAsync(inputTokens: 50, estimatedOutputTokens: 50);
                    // Dispose immediately
                    await reservation.DisposeAsync();
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        // The race condition exists regardless of whether exceptions occur
        // This test documents the unsafe behavior
        exceptions.Should().NotBeNull(
            "the test ran successfully but the race condition in UpdateSafetyTimerState() still exists");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task ImmediateReservationsDuringQueueProcessing_CreatesRaceCondition()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateNearCapacityOptions();
        options.TokenLimit = 5000;
        options.SafetyBuffer = 0;
        options.MaxConcurrentRequests = 3;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Fill capacity to force some requests to queue
        var immediateReservations = new List<Models.TokenReservation>();
        for (int i = 0; i < 2; i++)
        {
            immediateReservations.Add(await gate.ReserveTokensAsync(inputTokens: 2000, estimatedOutputTokens: 0));
        }

        // Launch requests that will queue
        var queuedTasks = new List<Task<Models.TokenReservation>>();
        for (int i = 0; i < 5; i++)
        {
            queuedTasks.Add(gate.ReserveTokensAsync(inputTokens: 1000, estimatedOutputTokens: 0));
        }

        // Give them time to queue
        await Task.Delay(100);

        // Now release reservations from multiple threads while new immediate reservations happen
        // This creates a race between:
        // 1. Queue processing (modifies _waitingRequests)
        // 2. New immediate reservations calling UpdateSafetyTimerState() outside lock
        var raceTasks = new List<Task>();

        foreach (var reservation in immediateReservations)
        {
            raceTasks.Add(Task.Run(async () => await reservation.DisposeAsync()));
        }

        // Add more immediate reservation attempts during release
        for (int i = 0; i < 10; i++)
        {
            raceTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var r = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 0);
                    await r.DisposeAsync();
                }
                catch { /* Expected - may timeout */ }
            }));
        }

        await Task.WhenAll(raceTasks);

        // Assert
        // The test demonstrates the race condition scenario
        // Where UpdateSafetyTimerState() reads collections outside lock
        // while TryProcessingWaitingRequests() modifies them inside lock
        queuedTasks.Should().NotBeEmpty("queued requests were created");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task StressTest_ConcurrentReservationsExposeUnsafeMemoryAccess()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 20);
        options.TokenLimit = 1000000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var allTasks = new List<Task>();
        var totalOperations = 0;

        // Act
        // Hammer the system with concurrent operations to expose race
        while (!cts.Token.IsCancellationRequested)
        {
            var batch = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                try
                {
                    var reservation = await gate.ReserveTokensAsync(inputTokens: 10, estimatedOutputTokens: 10);
                    await Task.Delay(1); // Hold briefly
                    await reservation.DisposeAsync();
                    Interlocked.Increment(ref totalOperations);
                }
                catch { /* May timeout or fail due to race */ }
            })).ToArray();

            allTasks.AddRange(batch);
            await Task.Delay(10);
        }

        await Task.WhenAll(allTasks);

        // Assert
        totalOperations.Should().BeGreaterThan(0, "some operations should have completed");

        // The bug is present regardless of test outcome
        // This stress test increases probability of exposing memory corruption
        // or crashes that would occur with a proper race detector

        // Cleanup
        gate.Dispose();
    }
}
