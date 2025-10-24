using FluentAssertions;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-1: Semaphore leak in immediate reservation path causes deadlock
///
/// Bug Description:
/// When TryReserveImmediately() succeeds (line 96) or double-check succeeds (line 111),
/// the method returns without releasing the _concurrencyLimiter semaphore. After MaxConcurrentRequests
/// successful immediate reservations, the semaphore is permanently exhausted and all subsequent requests hang forever.
///
/// Expected Behavior (after fix):
/// Semaphore should be released when TokenReservation is disposed, not at the end of ReserveTokensAsync.
/// </summary>
public class TokenRateGate_1_SemaphoreLeakTests
{
    [Fact]
    public async Task SemaphoreLeakAfterImmediateReservations_CausesDeadlock()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 2);
        options.TokenLimit = 100000; // High enough to allow immediate reservations
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act & Assert
        // Make MaxConcurrentRequests (2) successful immediate reservations
        var reservation1 = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        var reservation2 = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);

        // BUG: The semaphore is now exhausted because it was never released
        // Third request should wait, but in the current buggy implementation it will hang forever
        // because the semaphore will never be released

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
        var reservationTask = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);

        var completedTask = await Task.WhenAny(reservationTask, timeoutTask);

        // BUG VERIFICATION: The reservation task should complete quickly since we have capacity,
        // but it hangs because the semaphore was leaked
        completedTask.Should().Be(timeoutTask,
            "because the semaphore was leaked and the third request hangs forever waiting for semaphore availability");

        // Cleanup
        await reservation1.DisposeAsync();
        await reservation2.DisposeAsync();
        gate.Dispose();
    }

    [Fact]
    public async Task SemaphoreLeakOnDisposal_AllowsSubsequentRequests()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 2);
        options.TokenLimit = 100000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Make immediate reservation and immediately dispose
        var reservation1 = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        await reservation1.DisposeAsync(); // This SHOULD release the semaphore

        var reservation2 = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        await reservation2.DisposeAsync();

        // Now try MaxConcurrentRequests + 1 concurrent reservations
        var reservation3Task = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        var reservation4Task = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        var reservation5Task = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
        var allReservations = Task.WhenAll(reservation3Task, reservation4Task, reservation5Task);
        var completedTask = await Task.WhenAny(allReservations, timeoutTask);

        // Assert
        // EXPECTED AFTER FIX: All reservations should complete quickly
        // BUG: Some might hang because semaphore is leaked
        if (completedTask == timeoutTask)
        {
            // This verifies the bug exists
            allReservations.IsCompleted.Should().BeFalse(
                "because the semaphore leak prevents additional requests even after previous reservations were disposed");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task MultipleConcurrentImmediateReservations_ExhaustsSemaphore()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 3);
        options.TokenLimit = 1000000; // Very high to ensure immediate reservations
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Launch MaxConcurrentRequests reservations that succeed immediately
        var tasks = Enumerable.Range(0, 3)
            .Select(_ => gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100))
            .ToArray();

        var allReservations = await Task.WhenAll(tasks);
        allReservations.Should().HaveCount(3, "all immediate reservations should succeed");

        // Now try one more request - it should hang due to semaphore exhaustion
        var extraReservationTask = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));

        var completedTask = await Task.WhenAny(extraReservationTask, timeoutTask);

        // Assert - Bug verification
        completedTask.Should().Be(timeoutTask,
            "because the semaphore is exhausted after MaxConcurrentRequests immediate reservations");

        extraReservationTask.IsCompleted.Should().BeFalse(
            "the request is stuck waiting for semaphore availability that will never come");

        // Cleanup
        foreach (var reservation in allReservations)
        {
            await reservation.DisposeAsync();
        }
        gate.Dispose();
    }

    [Fact]
    public async Task ImmediateReservationFollowedByDispose_ShouldReleaseSemaphore()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateHighConcurrencyOptions(maxConcurrent: 1);
        options.TokenLimit = 100000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act - Perform multiple reserve-dispose cycles
        for (int i = 0; i < 5; i++)
        {
            var reservation = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
            await reservation.DisposeAsync();
        }

        // Try one more reservation with timeout
        var finalReservationTask = gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 100);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));

        var completedTask = await Task.WhenAny(finalReservationTask, timeoutTask);

        // Assert
        if (completedTask == timeoutTask)
        {
            // BUG VERIFIED: Even after disposing previous reservations, the semaphore is stuck
            finalReservationTask.IsCompleted.Should().BeFalse(
                "because the semaphore was never released during immediate reservation success path");
        }
        else
        {
            // Expected behavior after fix
            finalReservationTask.IsCompletedSuccessfully.Should().BeTrue(
                "because disposing reservations should have released the semaphore");
            await finalReservationTask.Result.DisposeAsync();
        }

        // Cleanup
        gate.Dispose();
    }
}
