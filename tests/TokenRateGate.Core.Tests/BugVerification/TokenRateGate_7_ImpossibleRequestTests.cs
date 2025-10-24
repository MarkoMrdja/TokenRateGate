using FluentAssertions;
using System.Diagnostics;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-7: No validation for impossible requests (tokens > limit)
///
/// Bug Description:
/// ReserveTokensAsync() doesn't validate if requested tokens exceed the token limit.
/// Impossible requests wait for MaxWaitTime (2 min default) before timing out with misleading error.
///
/// Location: TokenRateGate.cs:78-90 (validation section)
/// Impact: Wastes semaphore slots, poor UX, misleading error messages
///
/// Expected Behavior (after fix):
/// Add validation: totalEstimatedTokens <= (TokenLimit - SafetyBuffer) and fail fast with clear error.
/// </summary>
public class TokenRateGate_7_ImpossibleRequestTests
{
    [Fact]
    public async Task RequestExceedingTokenLimit_WaitsUntilTimeout()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        options.MaxWaitTime = TimeSpan.FromSeconds(2); // Short timeout for faster test
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Request that exceeds effective limit (TokenLimit - SafetyBuffer = 9000)
        var impossibleTokens = 12000; // More than TokenLimit itself

        var stopwatch = Stopwatch.StartNew();
        Exception? capturedException = null;

        try
        {
            // BUG: This request can NEVER be satisfied, but instead of failing fast,
            // it waits for MaxWaitTime (2 seconds) before timing out
            await gate.ReserveTokensAsync(inputTokens: impossibleTokens, estimatedOutputTokens: 0);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        stopwatch.Stop();

        // Assert
        // BUG VERIFICATION: Request should fail immediately, but instead waits full timeout
        stopwatch.Elapsed.Should().BeCloseTo(options.MaxWaitTime, precision: TimeSpan.FromMilliseconds(500),
            "because the impossible request waited the full MaxWaitTime before failing");

        capturedException.Should().NotBeNull("impossible request should eventually fail");
        capturedException.Should().BeOfType<TimeoutException>(
            "because the current implementation throws TimeoutException after MaxWaitTime");

        // BUG: Error message doesn't explain that the request is impossible
        capturedException!.Message.Should().Contain("Unable to acquire token capacity",
            "current error message is misleading - says 'unable to acquire' not 'impossible'");

        Console.WriteLine($"BUG: Impossible request waited {stopwatch.ElapsedMilliseconds}ms before failing");
        Console.WriteLine($"Error: {capturedException.Message}");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task RequestExceedingEffectiveLimit_WaitsUntilTimeout()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 2000; // Effective limit is 8000
        options.MaxWaitTime = TimeSpan.FromSeconds(2);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Request that exceeds effective limit but is under TokenLimit
        var impossibleTokens = 9000; // > (TokenLimit - SafetyBuffer) but < TokenLimit

        var stopwatch = Stopwatch.StartNew();
        Exception? capturedException = null;

        try
        {
            await gate.ReserveTokensAsync(inputTokens: impossibleTokens, estimatedOutputTokens: 0);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        stopwatch.Stop();

        // Assert
        // BUG: This request can never be satisfied because effective limit is 8000
        // Should fail immediately with clear message
        // Instead: waits 2 seconds then throws misleading TimeoutException

        stopwatch.Elapsed.Should().BeCloseTo(options.MaxWaitTime, precision: TimeSpan.FromMilliseconds(500),
            "because request exceeding effective limit waits full timeout");

        capturedException.Should().BeOfType<TimeoutException>(
            "current buggy behavior throws TimeoutException");

        // EXPECTED AFTER FIX: ArgumentException with clear message like:
        // "Requested tokens (9000) exceeds the effective token limit (8000).
        //  This request can never be satisfied."

        Console.WriteLine($"BUG: Request for {impossibleTokens} tokens (effective limit: 8000) waited {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task ImpossibleRequest_WastesSemaphoreSlot()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        options.MaxConcurrentRequests = 2;
        options.MaxWaitTime = TimeSpan.FromSeconds(3);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Start an impossible request that will occupy a semaphore slot
        var impossibleTask = Task.Run(async () =>
        {
            try
            {
                await gate.ReserveTokensAsync(inputTokens: 20000, estimatedOutputTokens: 0); // Impossible
            }
            catch { }
        });

        await Task.Delay(100); // Let it acquire semaphore

        // Now try a valid request
        var validRequestStopwatch = Stopwatch.StartNew();
        var validReservation = await gate.ReserveTokensAsync(inputTokens: 1000, estimatedOutputTokens: 0);
        validRequestStopwatch.Stop();

        await validReservation.DisposeAsync();
        await impossibleTask; // Wait for impossible request to timeout

        // Assert
        // BUG IMPACT: The impossible request holds a semaphore slot for 3 seconds
        // This blocks valid requests from making progress efficiently
        // With MaxConcurrentRequests=2, one slot is wasted for 3 seconds

        Console.WriteLine($"Valid request completed in {validRequestStopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine("BUG: Impossible request wasted a semaphore slot for 3 seconds");

        validRequestStopwatch.ElapsedMilliseconds.Should().BeLessThan(1000,
            "valid request should complete quickly");

        // IMPACT: In production with many impossible requests, this could exhaust all semaphore slots
        // and block the entire system

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task MultipleImpossibleRequests_ExhaustSemaphore()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        options.MaxConcurrentRequests = 3;
        options.MaxWaitTime = TimeSpan.FromSeconds(5);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Launch MaxConcurrentRequests impossible requests
        var impossibleTasks = new List<Task>();
        for (int i = 0; i < 3; i++)
        {
            impossibleTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await gate.ReserveTokensAsync(inputTokens: 20000, estimatedOutputTokens: 0);
                }
                catch { }
            }));
        }

        await Task.Delay(200); // Let them all acquire semaphore slots

        // Now try a valid request - it should be blocked!
        var validRequestTask = gate.ReserveTokensAsync(inputTokens: 1000, estimatedOutputTokens: 0);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(1));

        var completedTask = await Task.WhenAny(validRequestTask, timeoutTask);

        // Assert
        // BUG VERIFICATION: Valid request is blocked because all semaphore slots
        // are held by impossible requests that will timeout in 5 seconds

        completedTask.Should().Be(timeoutTask,
            "because all semaphore slots are held by impossible requests waiting to timeout");

        validRequestTask.IsCompleted.Should().BeFalse(
            "valid request cannot proceed because semaphore is exhausted by impossible requests");

        Console.WriteLine("BUG IMPACT: Valid requests blocked by impossible requests holding semaphore slots");

        // Wait for impossible requests to timeout
        await Task.WhenAll(impossibleTasks);

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EdgeCase_RequestEqualsEffectiveLimit_ShouldSucceed()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Request exactly at effective limit (should be valid)
        var exactLimit = 9000; // TokenLimit - SafetyBuffer

        var reservation = await gate.ReserveTokensAsync(inputTokens: exactLimit, estimatedOutputTokens: 0);

        // Assert
        reservation.Should().NotBeNull("request at exact effective limit should succeed");
        reservation.ReservedTokens.Should().Be(exactLimit);

        await reservation.DisposeAsync();

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EdgeCase_RequestOneOverEffectiveLimit_ShouldFailFast()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        options.MaxWaitTime = TimeSpan.FromSeconds(2);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Request one token over effective limit
        var oneOver = 9001; // (TokenLimit - SafetyBuffer) + 1

        var stopwatch = Stopwatch.StartNew();
        Exception? capturedException = null;

        try
        {
            await gate.ReserveTokensAsync(inputTokens: oneOver, estimatedOutputTokens: 0);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        stopwatch.Stop();

        // Assert
        // BUG: Should fail immediately with ArgumentException
        // Current behavior: Waits 2 seconds then throws TimeoutException

        stopwatch.Elapsed.Should().BeCloseTo(TimeSpan.FromSeconds(2), precision: TimeSpan.FromMilliseconds(500),
            "because impossible request waits full timeout");

        capturedException.Should().BeOfType<TimeoutException>();

        // EXPECTED AFTER FIX:
        // stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
        // capturedException.Should().BeOfType<ArgumentException>();

        Console.WriteLine($"BUG: Request for {oneOver} tokens (limit: 9000) waited {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task ImpossibleRequestWithEstimatedOutput_AlsoWaitsUntilTimeout()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        options.MaxWaitTime = TimeSpan.FromSeconds(2);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Total (input + output) exceeds limit
        var impossibleInput = 6000;
        var impossibleOutput = 5000; // Total: 11000 > effective limit (9000)

        var stopwatch = Stopwatch.StartNew();
        Exception? capturedException = null;

        try
        {
            await gate.ReserveTokensAsync(inputTokens: impossibleInput, estimatedOutputTokens: impossibleOutput);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        stopwatch.Stop();

        // Assert
        // BUG: Should validate total tokens and fail fast
        stopwatch.Elapsed.Should().BeCloseTo(options.MaxWaitTime, precision: TimeSpan.FromMilliseconds(500));
        capturedException.Should().BeOfType<TimeoutException>();

        Console.WriteLine($"BUG: Impossible request (input:{impossibleInput} + output:{impossibleOutput} = 11000) waited {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task UserExperience_UnclearErrorMessage()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        options.MaxWaitTime = TimeSpan.FromSeconds(2);
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        Exception? capturedException = null;
        try
        {
            await gate.ReserveTokensAsync(inputTokens: 15000, estimatedOutputTokens: 0);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // Assert
        capturedException.Should().NotBeNull();
        var message = capturedException!.Message;

        // BUG: Error message is misleading
        message.Should().Contain("Unable to acquire token capacity after waiting");
        message.Should().Contain("Maximum wait time");

        // This suggests the system was busy, not that the request is fundamentally impossible
        Console.WriteLine($"Current misleading error: {message}");

        // EXPECTED AFTER FIX:
        // "Requested tokens (15000) exceeds the effective token limit (9000).
        //  Token limit: 10000, Safety buffer: 1000. This request can never be satisfied."

        // Cleanup
        gate.Dispose();
    }
}
