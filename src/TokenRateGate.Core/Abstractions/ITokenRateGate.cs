using TokenRateGate.Core.Models;

namespace TokenRateGate.Core.Abstractions;

public interface ITokenRateGate
{
    /// <summary>
    /// Reserves tokens for an LLM request
    /// </summary>
    /// <param name="inputTokens">Actual input tokens (prompt + system message)</param>
    /// <param name="estimatedOutputTokens">Expected output tokens (0 = use default estimation)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Token reservation that must be disposed after use</returns>
    Task<TokenReservation> ReserveTokensAsync(int inputTokens, int estimatedOutputTokens = 0, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets current token usage within the time window.
    /// Note: Statistics are eventually consistent. During idle periods (no active requests),
    /// expired records may not be cleaned until the next request arrives.
    /// </summary>
    int GetCurrentUsage();

    /// <summary>
    /// Gets currently reserved tokens.
    /// Note: Statistics are eventually consistent. During idle periods (no active requests),
    /// expired records may not be cleaned until the next request arrives.
    /// </summary>
    int GetReservedTokens();

    /// <summary>
    /// Gets comprehensive usage statistics.
    /// Note: Statistics are eventually consistent. During idle periods (no active requests),
    /// expired records may not be cleaned until the next request arrives. This is by design
    /// to avoid unnecessary background processing when the system is idle.
    /// </summary>
    TokenUsageStats GetUsageStats();
}