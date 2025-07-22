namespace TokenRateGate.Core.Utils;

public enum OutputEstimationStrategy
{
    /// <summary>
    /// Multiply input tokens by a fixed multiplier (default: 0.5 = 50% of input)
    /// Good for: Chat completions, Q&A where output is typically shorter than input
    /// </summary>
    FixedMultiplier,

    /// <summary>
    /// Add a fixed number of tokens regardless of input size
    /// Good for: Known output patterns, structured responses
    /// </summary>
    FixedAmount,

    /// <summary>
    /// Conservative approach: assume output equals input (2x total)
    /// Good for: When you're unsure, better to overestimate
    /// </summary>
    Conservative
}
