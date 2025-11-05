using Microsoft.Extensions.Logging;
using NSubstitute;
using TokenRateGate.Core.Timer;
using Xunit;

namespace TokenRateGate.Core.Tests;

public class SafetyTimerManagerTests
{
    private readonly ILogger _logger;

    public SafetyTimerManagerTests()
    {
        _logger = Substitute.For<ILogger>();
    }

    [Fact]
    public void Constructor_WithNullCallback_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new SafetyTimerManager(null!, _logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        Action callback = () => { };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new SafetyTimerManager(callback, null!));
    }

    [Fact]
    public void UpdateState_WithWaitingRequests_StartsTimer()
    {
        // Arrange
        var callbackInvoked = false;
        Action callback = () => { callbackInvoked = true; };
        using var manager = new SafetyTimerManager(callback, _logger);

        // Act
        manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 0, waitingRequestCount: 1);

        // Wait for timer to fire (100ms interval + buffer)
        Thread.Sleep(250);

        // Assert
        Assert.True(callbackInvoked, "Timer callback should have been invoked when there are waiting requests");
    }

    [Fact]
    public void UpdateState_WithNoWaitingRequests_StopsTimer()
    {
        // Arrange
        var callbackCount = 0;
        Action callback = () => { Interlocked.Increment(ref callbackCount); };
        using var manager = new SafetyTimerManager(callback, _logger);

        // Start timer
        manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 0, waitingRequestCount: 1);
        Thread.Sleep(250);
        var countAfterStart = callbackCount;
        Assert.True(countAfterStart > 0, "Timer should have fired after starting");

        // Stop timer
        manager.UpdateState(hasWaitingRequests: false, activeReservationCount: 0, waitingRequestCount: 0);
        Thread.Sleep(250);
        var countAfterStop = callbackCount;

        // Assert - count should not increase significantly after stopping
        Assert.True(countAfterStop <= countAfterStart + 2,
            $"Timer should have stopped. Count after start: {countAfterStart}, Count after stop: {countAfterStop}");
    }

    [Fact]
    public void Stop_StopsTimerImmediately()
    {
        // Arrange
        var callbackCount = 0;
        Action callback = () => { Interlocked.Increment(ref callbackCount); };
        using var manager = new SafetyTimerManager(callback, _logger);

        // Start timer
        manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 0, waitingRequestCount: 1);
        Thread.Sleep(250);
        var countAfterStart = callbackCount;
        Assert.True(countAfterStart > 0, "Timer should have fired after starting");

        // Stop timer explicitly
        manager.Stop();
        Thread.Sleep(250);
        var countAfterStop = callbackCount;

        // Assert - count should not increase after explicit stop
        Assert.True(countAfterStop <= countAfterStart + 1,
            $"Timer should have stopped immediately. Count after start: {countAfterStart}, Count after stop: {countAfterStop}");
    }

    [Fact]
    public void UpdateState_AfterDispose_DoesNotThrow()
    {
        // Arrange
        Action callback = () => { };
        var manager = new SafetyTimerManager(callback, _logger);
        manager.Dispose();

        // Act & Assert - should not throw
        manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 1, waitingRequestCount: 1);
    }

    [Fact]
    public void Stop_AfterDispose_DoesNotThrow()
    {
        // Arrange
        Action callback = () => { };
        var manager = new SafetyTimerManager(callback, _logger);
        manager.Dispose();

        // Act & Assert - should not throw
        manager.Stop();
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        Action callback = () => { };
        var manager = new SafetyTimerManager(callback, _logger);

        // Act & Assert - should not throw
        manager.Dispose();
        manager.Dispose();
        manager.Dispose();
    }

    [Fact]
    public void TimerCallback_WhenExceptionThrown_LogsError()
    {
        // Arrange
        var exception = new InvalidOperationException("Test exception");
        Action callback = () => throw exception;
        using var manager = new SafetyTimerManager(callback, _logger);

        // Act
        manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 0, waitingRequestCount: 1);
        Thread.Sleep(250);

        // Assert - verify error was logged
        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Safety timer error")),
            Arg.Is<Exception>(ex => ex == exception),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void UpdateState_WithActiveReservations_StartsTimer()
    {
        // Arrange
        var callbackInvoked = false;
        Action callback = () => { callbackInvoked = true; };
        using var manager = new SafetyTimerManager(callback, _logger);

        // Act - waiting requests with active reservations should start timer
        manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 5, waitingRequestCount: 3);

        // Wait for timer to fire
        Thread.Sleep(250);

        // Assert
        Assert.True(callbackInvoked, "Timer should start when there are waiting requests regardless of active reservations");
    }

    [Fact]
    public void UpdateState_MultipleStartStopCycles_WorksCorrectly()
    {
        // Arrange
        var callbackCount = 0;
        Action callback = () => { Interlocked.Increment(ref callbackCount); };
        using var manager = new SafetyTimerManager(callback, _logger);

        // Act - multiple start/stop cycles
        for (int i = 0; i < 3; i++)
        {
            // Start
            manager.UpdateState(hasWaitingRequests: true, activeReservationCount: 0, waitingRequestCount: 1);
            Thread.Sleep(150);
            Assert.True(callbackCount > 0, $"Timer should fire in cycle {i + 1}");

            // Stop
            var countBeforeStop = callbackCount;
            manager.UpdateState(hasWaitingRequests: false, activeReservationCount: 0, waitingRequestCount: 0);
            Thread.Sleep(150);

            // Verify timer stopped (allow small margin for race condition)
            Assert.True(callbackCount <= countBeforeStop + 2,
                $"Timer should stop in cycle {i + 1}. Count before: {countBeforeStop}, after: {callbackCount}");
        }
    }
}
