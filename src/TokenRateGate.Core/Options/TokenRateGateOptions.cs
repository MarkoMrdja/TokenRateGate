using TokenRateGate.Core.Abstractions;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.Core.Options;

public class TokenRateGateOptions
{
    #region Core rate limiting
    /// <summary>
    /// Maximum number of tokens that can be consumed within the specified time window.
    /// This should match your API provider's token-per-minute (TPM) limit.
    /// </summary>
    public int TokenLimit { get; set; } = 1_000_000;
    
    /// <summary>
    /// Time window in seconds over which the TokenLimit is enforced.
    /// Most API providers use 60 seconds (1 minute) as their rate limiting window.
    /// </summary>
    public int WindowSeconds { get; set; } = 60;
    
    /// <summary>
    /// Percentage of TokenLimit to reserve as a safety buffer to prevent hitting exact API limits.
    /// This accounts for timing variations and measurement inaccuracies.
    /// Value should be between 0.0 (0%) and 1.0 (100%).
    /// Default: 0.05 (5% of the token limit).
    /// </summary>
    public double SafetyBufferPercentage { get; set; } = 0.05;

    #endregion
    
    #region Concurrency and request limiting
    
    /// <summary>
    /// Maximum number of simultaneous token reservation attempts allowed.
    /// Controls system load and prevents resource exhaustion under high concurrency.
    /// This limits both active reservations and queued requests waiting for capacity.
    /// Default: 1000. Maximum allowed: 10,000 (enforced for safety).
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1000;
    
    /// <summary>
    /// Maximum number of API requests allowed per minute, separate from token limits.
    /// Many providers enforce both token-per-minute AND request-per-minute limits.
    /// </summary>
    public int MaxRequestsPerMinute { get; set; } = 1_000;
    
    /// <summary>
    /// Time window in seconds for enforcing MaxRequestsPerMinute.
    /// Most API providers use 60 seconds regardless of token limit windows.
    /// Default: 60 seconds.
    /// </summary>
    public int RequestWindowSeconds { get; set; } = 60;
    
    #endregion
    
    /// <summary>
    /// Maximum time a request can wait for capacity before timing out.
    /// Set to null to allow requests to wait indefinitely (true "gate" behavior - all requests eventually succeed).
    /// Set to a TimeSpan value to fail requests that can't acquire capacity within that time (circuit breaker pattern).
    /// Default: 2 minutes.
    ///
    /// Note: This timeout applies from the moment ReserveTokensAsync() is called, including time waiting
    /// for the concurrency semaphore AND time waiting in the capacity queue.
    /// </summary>
    public TimeSpan? MaxWaitTime { get; set; } = TimeSpan.FromMinutes(2);

    #region Token estimation

    /// <summary>
    /// Token estimator for counting tokens in text inputs.
    /// Used to estimate token counts when not provided explicitly by the caller.
    /// If null, a default CharacterBasedTokenEstimator (4 chars per token) will be used.
    /// Set this to use provider-specific estimators (e.g., tiktoken for OpenAI).
    /// </summary>
    public ITokenEstimator? TokenEstimator { get; set; }

    /// <summary>
    /// Strategy for estimating output tokens when not explicitly provided.
    /// Affects reservation accuracy and system efficiency.
    /// Default: FixedMultiplier.
    /// </summary>
    public OutputEstimationStrategy OutputEstimationStrategy { get; set; } = OutputEstimationStrategy.FixedMultiplier;

    /// <summary>
    /// Multiplier for input tokens to estimate output tokens (default: 0.5)
    /// Used when OutputEstimationStrategy is FixedMultiplier
    /// Examples: 0.3 = 30% of input, 1.0 = 100% of input, 2.0 = 200% of input
    /// </summary>
    public double OutputMultiplier { get; set; } = 0.5;

    /// <summary>
    /// Fixed number of tokens to add for output estimation (default: 1000)
    /// Used when OutputEstimationStrategy is FixedAmount
    /// Best when you've tested the model with a similar data and know the approximate output size
    /// </summary>
    public int DefaultOutputTokens { get; set; } = 1000;

    #endregion

    /// <summary>
    /// Optional OpenAI-specific configuration.
    /// Used when rate-limiting OpenAI API calls.
    /// </summary>
    public OpenAIConfiguration? OpenAI { get; set; }

    /// <summary>
    /// Optional Azure OpenAI-specific configuration.
    /// Used when rate-limiting Azure OpenAI API calls.
    /// </summary>
    public AzureConfiguration? Azure { get; set; }
}
