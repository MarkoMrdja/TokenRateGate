using TokenRateGate.Core.Abstractions;

namespace TokenRateGate.Core.Utils;

/// <summary>
/// Production implementation that returns real system time.
/// </summary>
internal sealed class SystemClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
