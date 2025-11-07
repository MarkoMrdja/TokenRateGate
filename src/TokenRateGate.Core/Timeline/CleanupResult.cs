namespace TokenRateGate.Core.Timeline;

/// <summary>
/// Result of cleanup operations indicating what was freed.
/// </summary>
internal sealed record CleanupResult(
    bool TokensFreed,
    bool RequestsFreed,
    int RemovedTokens,
    int RemovedRequests,
    int RemovedStaleReservations);
