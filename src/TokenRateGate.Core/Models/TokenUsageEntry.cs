namespace TokenRateGate.Core.Models;

/// <summary>
/// Represents a completed token usage entry in the sliding window timeline.
/// Used internally by TokenRateGate to track historical token consumption.
/// </summary>
internal sealed record TokenUsageEntry(
    DateTime Timestamp,
    int ActualTokens,
    int ReservedTokens);
