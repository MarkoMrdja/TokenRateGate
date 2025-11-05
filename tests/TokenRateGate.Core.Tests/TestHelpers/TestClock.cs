using TokenRateGate.Core.Abstractions;

namespace TokenRateGate.Core.Tests.TestHelpers;

/// <summary>
/// Test clock that allows manual time control for deterministic testing.
/// </summary>
public sealed class TestClock : ISystemClock
{
    private DateTime _currentTime = DateTime.UtcNow;

    public DateTime UtcNow => _currentTime;

    /// <summary>
    /// Advances the clock by the specified duration.
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        _currentTime = _currentTime.Add(duration);
    }

    /// <summary>
    /// Sets the clock to a specific time.
    /// </summary>
    public void SetTime(DateTime time)
    {
        _currentTime = time;
    }

    /// <summary>
    /// Resets the clock to the current system time.
    /// </summary>
    public void Reset()
    {
        _currentTime = DateTime.UtcNow;
    }
}
