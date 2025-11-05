namespace TokenRateGate.Core.Abstractions;

/// <summary>
/// Abstraction for system time to enable testable time-dependent code.
/// </summary>
public interface ISystemClock
{
    /// <summary>
    /// Gets the current UTC date and time.
    /// </summary>
    DateTime UtcNow { get; }
}
