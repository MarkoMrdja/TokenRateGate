using TokenRateGate.Core.Utils;

namespace TokenRateGate.Core.Options;

public class TokenRateGateOptions
{
    // Core rate limiting
    public int TokenLimit { get; set; } = 1_000_000;
    public int WindowSeconds { get; set; } = 60;
    public int SafetyBuffer { get; set; } = 50_000;

    // Concurrency and request limiting
    public int MaxConcurrentRequests { get; set; } = 1;
    public int MaxRequestsPerMinute { get; set; } = 1_000;

    // Token estimation strategy for the output
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
}
