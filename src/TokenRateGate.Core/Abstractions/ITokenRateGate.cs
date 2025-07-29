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
    /// Gets current token usage within the time window
    /// </summary>
    int GetCurrentUsage();
    
    /// <summary>
    /// Gets currently reserved tokens
    /// </summary>
    int GetReservedTokens();
    
    /// <summary>
    /// Gets comprehensive usage statistics
    /// </summary>
    TokenUsageStats GetUsageStats();
}