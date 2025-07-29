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
    /// Number of tokens to reserve as a safety buffer to prevent hitting exact API limits.
    /// This accounts for timing variations and measurement inaccuracies.
    /// </summary>
    public int SafetyBuffer { get; set; } = 50_000;

    #endregion
    
    #region Concurrency and request limiting
    
    /// <summary>
    /// Maximum number of simultaneous token reservation attempts allowed.
    /// Controls a system load and prevents resource exhaustion under high concurrency.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1;
    
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
    /// Maximum time a request can wait in the queue before timing out.
    /// Prevents requests from waiting indefinitely during high load or system issues.
    /// </summary>
    public TimeSpan MaxWaitTime { get; set; } = TimeSpan.FromMinutes(2);

    #region Token estimation strategy for the output
    
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
}
