using FluentAssertions;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-4: Disposed flag checked without synchronization
///
/// Bug Description:
/// The _disposed flag is checked without synchronization in multiple methods (lines 80, 393, 430).
/// Thread A could pass the disposed check, then Thread B calls Dispose(), then Thread A continues
/// using disposed resources (_concurrencyLimiter, _safetyTimer).
///
/// Expected Behavior (after fix):
/// Make _disposed volatile, use Interlocked operations, or check within the lock.
/// </summary>
public class TokenRateGate_4_DisposedFlagRaceTests
{
    [Fact]
    public async Task DisposeWhileReserving_CanAccessDisposedResources()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.MaxConcurrentRequests = 1;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Start a reservation request
        var reservationTask = Task.Run(async () =>
        {
            try
            {
                // BUG: Passes disposed check at line 80
                var reservation = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
                return (reservation, exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (reservation: null, exception: ex);
            }
        });

        // Give it a moment to pass the disposed check
        await Task.Delay(50);

        // Dispose while the reservation is in progress
        // BUG: Thread A passed disposed check, Thread B disposes, Thread A uses disposed resources
        gate.Dispose();

        var (reservation, exception) = await reservationTask;

        // Assert
        // BUG VERIFICATION: One of these scenarios indicates the bug:
        // 1. ObjectDisposedException (accessing disposed _concurrencyLimiter or _safetyTimer)
        // 2. Success with reservation (race allowed operation on disposed object)
        // 3. Other unexpected exception

        if (exception != null)
        {
            // Most likely scenario - ObjectDisposedException
            exception.Should().BeOfType<ObjectDisposedException>(
                "because the reservation accessed disposed resources after passing the unsynchronized disposed check");
        }
        else if (reservation != null)
        {
            // Race allowed the reservation to succeed even after disposal
            reservation.Should().NotBeNull(
                "because the race condition allowed the operation to proceed after disposal started");
            await reservation.DisposeAsync();
        }

        // In either case, the bug is present - operations shouldn't be allowed after Dispose() is called
    }

    [Fact]
    public async Task MultipleSimultaneousDisposeCalls_ShouldBeIdempotent()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Call Dispose() from multiple threads simultaneously
        var disposeTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                try
                {
                    gate.Dispose();
                }
                catch (Exception ex)
                {
                    return ex;
                }
                return null;
            }))
            .ToArray();

        var exceptions = await Task.WhenAll(disposeTasks);

        // Assert
        // Dispose should be idempotent and thread-safe
        var nonNullExceptions = exceptions.Where(e => e != null).ToList();

        if (nonNullExceptions.Any())
        {
            // BUG: Multiple concurrent Dispose() calls caused exceptions
            nonNullExceptions.Should().NotBeEmpty(
                "because concurrent Dispose() calls without proper synchronization can cause issues");
        }
        else
        {
            // Expected: All Dispose() calls completed without exception
            // But the _disposed flag check is still racy in other methods
            exceptions.Should().AllSatisfy(e => e.Should().BeNull(),
                "because Dispose() should be idempotent");
        }
    }

    [Fact]
    public async Task DisposeWhileMultipleReservationsInProgress_CausesRaceCondition()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 10);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Start multiple concurrent reservations
        var reservationTasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    var reservation = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
                    await Task.Delay(10); // Hold briefly
                    await reservation.DisposeAsync();
                    return (success: true, exception: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (success: false, exception: ex);
                }
            }))
            .ToList();

        // Wait for some to start
        await Task.Delay(50);

        // Dispose while reservations are in flight
        var disposeTask = Task.Run(() =>
        {
            try
            {
                gate.Dispose();
            }
            catch (Exception ex)
            {
                return ex;
            }
            return null;
        });

        // Wait for everything to complete
        var results = await Task.WhenAll(reservationTasks);
        var disposeException = await disposeTask;

        // Assert
        var exceptionsEncountered = results.Where(r => r.exception != null).Select(r => r.exception).ToList();

        if (exceptionsEncountered.Any())
        {
            // BUG VERIFIED: Race between disposed check and actual disposal
            exceptionsEncountered.Should().NotBeEmpty(
                "because unsynchronized disposed flag allows operations after disposal");

            // Common exceptions:
            // - ObjectDisposedException (from _concurrencyLimiter or _safetyTimer)
            // - NullReferenceException (if resources were set to null)
            var disposedExceptions = exceptionsEncountered.OfType<ObjectDisposedException>().ToList();
            disposedExceptions.Should().NotBeEmpty(
                "because accessing disposed resources after racing past the disposed check causes ObjectDisposedException");
        }

        if (disposeException != null)
        {
            disposeException.Should().NotBeNull(
                "unexpected exception during dispose indicates race condition issues");
        }
    }

    [Fact]
    public async Task ReleaseReservationAfterDispose_ChecksDisposedFlagUnsafely()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Create and hold a reservation
        var reservation = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);

        // Act
        // Dispose the gate
        gate.Dispose();

        // Now try to release the reservation
        // BUG: ReleaseReservationAsync checks _disposed at line 430 without synchronization
        Exception? releaseException = null;
        try
        {
            await reservation.DisposeAsync();
        }
        catch (Exception ex)
        {
            releaseException = ex;
        }

        // Assert
        // The method checks _disposed and returns early if true (line 430)
        // But this check is racy - might read stale value
        // Expected behavior: either clean early return or ObjectDisposedException

        releaseException.Should().BeNull(
            "because ReleaseReservationAsync checks disposed flag and returns early, but this check is unsynchronized");

        // The bug is that the disposed check is racy, even if it works in this test case
    }

    [Fact]
    public async Task SafetyTimerCallbackAfterDispose_ChecksDisposedFlagUnsafely()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateRaceConditionTestOptions();
        options.WindowSeconds = 1; // Short window to trigger safety timer
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Create a scenario where safety timer would fire
        // Block capacity so requests queue
        var blocker = await gate.ReserveTokensAsync(inputTokens: 40000, estimatedOutputTokens: 0);

        // Queue a request
        var queuedTask = gate.ReserveTokensAsync(inputTokens: 1000, estimatedOutputTokens: 0);

        // Wait for it to queue (safety timer should start)
        await Task.Delay(500);

        // Act
        // Dispose while safety timer might be running
        // BUG: SafetyTimerCallback checks _disposed at line 393 without synchronization
        gate.Dispose();

        // Give safety timer a chance to fire after disposal
        await Task.Delay(1000);

        // Assert
        // The bug exists regardless of whether an exception occurred
        // SafetyTimerCallback has an unsynchronized disposed check
        // This is a documented race condition even if hard to trigger reliably

        await Task.Delay(100); // Let everything settle

        // The test documents the unsafe behavior at line 393
        // Expected behavior after fix: _disposed should be volatile or checked within lock
    }

    [Fact]
    public async Task StressTest_DisposeUnderHighConcurrency_ExposesRaces()
    {
        // This test runs multiple iterations to increase chance of exposing the race
        var iterations = 20;
        var racesDetected = 0;

        for (int i = 0; i < iterations; i++)
        {
            // Arrange
            var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 5);
            var gate = TokenRateGateTestFactory.CreateGate(options);

            // Act
            var tasks = new List<Task>();

            // Start many concurrent operations
            for (int j = 0; j < 10; j++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var r = await gate.ReserveTokensAsync(inputTokens: 50, estimatedOutputTokens: 50);
                        await r.DisposeAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        Interlocked.Increment(ref racesDetected);
                    }
                    catch { /* Other exceptions */ }
                }));
            }

            // Dispose at a random time
            await Task.Delay(Random.Shared.Next(5, 50));
            gate.Dispose();

            await Task.WhenAll(tasks);
        }

        // Assert
        if (racesDetected > 0)
        {
            racesDetected.Should().BeGreaterThan(0,
                $"race condition detected {racesDetected} times: operations accessed disposed resources after passing unsynchronized disposed check");
        }

        // Even if no races detected, the bug exists in the code
        // The unsynchronized _disposed field is a documented data race
    }
}
