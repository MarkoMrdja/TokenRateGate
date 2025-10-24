using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace TokenRateGate.Core.Tests.TestHelpers;

/// <summary>
/// Test logger that captures log messages for verification
/// </summary>
public class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentBag<LogEntry> _logEntries = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _logEntries.Add(new LogEntry
        {
            LogLevel = logLevel,
            EventId = eventId,
            Message = formatter(state, exception),
            Exception = exception,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Gets all captured log entries
    /// </summary>
    public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();

    /// <summary>
    /// Gets log entries of a specific level
    /// </summary>
    public IEnumerable<LogEntry> GetEntriesOfLevel(LogLevel level) =>
        _logEntries.Where(e => e.LogLevel == level);

    /// <summary>
    /// Gets log entries containing specific text
    /// </summary>
    public IEnumerable<LogEntry> GetEntriesContaining(string text) =>
        _logEntries.Where(e => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Clears all captured log entries
    /// </summary>
    public void Clear() => _logEntries.Clear();

    public class LogEntry
    {
        public LogLevel LogLevel { get; init; }
        public EventId EventId { get; init; }
        public string Message { get; init; } = string.Empty;
        public Exception? Exception { get; init; }
        public DateTime Timestamp { get; init; }
    }
}
