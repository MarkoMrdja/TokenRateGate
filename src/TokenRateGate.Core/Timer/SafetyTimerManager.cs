using Microsoft.Extensions.Logging;

namespace TokenRateGate.Core.Timer;

/// <summary>
/// Manages the safety timer that handles deadlock detection and queue processing
/// when requests are waiting but no active work is processing them.
/// </summary>
/// <remarks>
/// The safety timer serves two critical purposes:
/// 1. Deadlock Detection: When all active work completes but requests are still waiting,
///    the timer triggers cleanup to expire old timestamps and free capacity.
/// 2. RPM Limit Handling: When request-per-minute limits are reached, the timer periodically
///    checks if request timestamps have expired to allow processing waiting requests.
///
/// The timer uses a 100ms interval for responsive queue processing and automatically
/// starts when requests are waiting and stops when the queue is empty.
/// </remarks>
internal sealed class SafetyTimerManager : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly ILogger _logger;
    private readonly Action _timerCallback;
    private volatile bool _disposed;

    private const int TimerIntervalMs = 100;

    /// <summary>
    /// Creates a new SafetyTimerManager instance.
    /// </summary>
    /// <param name="timerCallback">The action to invoke when the timer fires. This should handle cleanup and queue processing.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public SafetyTimerManager(Action timerCallback, ILogger logger)
    {
        _timerCallback = timerCallback ?? throw new ArgumentNullException(nameof(timerCallback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = new System.Threading.Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Updates the timer state based on the current system state.
    /// </summary>
    /// <param name="hasWaitingRequests">Whether there are requests waiting in the queue.</param>
    /// <param name="activeReservationCount">Number of currently active reservations.</param>
    /// <param name="waitingRequestCount">Number of requests in the waiting queue.</param>
    public void UpdateState(bool hasWaitingRequests, int activeReservationCount, int waitingRequestCount)
    {
        if (_disposed) return;

        // Run timer when we have waiting requests - handles two scenarios:
        // 1. No active work (deadlock detection - all requests done but timestamps haven't expired)
        // 2. Active work exists but RPM limit blocks new requests (timestamps need to expire)
        bool shouldRunTimer = hasWaitingRequests;

        if (shouldRunTimer)
        {
            // Use a shorter interval (100ms) for the safety timer to ensure it triggers quickly
            _timer.Change(TimeSpan.FromMilliseconds(TimerIntervalMs), TimeSpan.FromMilliseconds(TimerIntervalMs));
            _logger.LogDebug("Safety timer STARTED (waiting: {WaitingCount}, active: {ActiveCount})",
                            waitingRequestCount, activeReservationCount);
        }
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (hasWaitingRequests || activeReservationCount > 0)
                _logger.LogDebug("Safety timer STOPPED (waiting: {WaitingCount}, active: {ActiveCount})",
                                waitingRequestCount, activeReservationCount);
        }
    }

    /// <summary>
    /// Stops the timer immediately.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTimerCallback(object? state)
    {
        if (_disposed) return;

        try
        {
            _timerCallback();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Safety timer error");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
