namespace TokenRateGate.Abstractions;

/// <summary>
/// Provides token-based rate limiting for LLM API requests.
/// Manages token reservations, queuing, and usage tracking to prevent exceeding API provider limits.
/// </summary>
public interface ITokenRateGate
{
    /// <summary>
    /// Reserves tokens for an LLM request.
    /// If capacity is available, returns immediately. Otherwise, queues the request until capacity becomes available.
    /// </summary>
    /// <param name="inputTokens">Actual input tokens (prompt + system message)</param>
    /// <param name="estimatedOutputTokens">Expected output tokens (0 = use default estimation strategy)</param>
    /// <param name="cancellationToken">Cancellation token to abort waiting for capacity</param>
    /// <returns>Token reservation that must be disposed after use</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when token counts are negative</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancelled or max wait time exceeded</exception>
    Task<ITokenReservation> ReserveTokensAsync(
        int inputTokens,
        int estimatedOutputTokens = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current token usage within the time window from completed requests.
    /// Note: Statistics are eventually consistent. During idle periods (no active requests),
    /// expired records may not be cleaned until the next request arrives.
    /// </summary>
    /// <returns>The number of tokens consumed by completed requests in the current window</returns>
    long GetCurrentUsage();

    /// <summary>
    /// Gets the total number of tokens currently reserved by active requests.
    /// Note: Statistics are eventually consistent. During idle periods (no active requests),
    /// expired records may not be cleaned until the next request arrives.
    /// </summary>
    /// <returns>The number of tokens reserved but not yet released</returns>
    long GetReservedTokens();

    /// <summary>
    /// Gets comprehensive usage statistics including current usage, reserved tokens, and capacity.
    /// Note: Statistics are eventually consistent. During idle periods (no active requests),
    /// expired records may not be cleaned until the next request arrives. This is by design
    /// to avoid unnecessary background processing when the system is idle.
    /// </summary>
    /// <returns>Complete statistics about token usage and system state</returns>
    ITokenUsageStats GetUsageStats();
}
