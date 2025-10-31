namespace TokenRateGate.Core.Abstractions;

/// <summary>
/// Provides token estimation for text inputs across different models and providers.
/// This abstraction enables support for arbitrary models without tight coupling to specific providers.
/// </summary>
public interface ITokenEstimator
{
    /// <summary>
    /// Estimates the number of tokens in the given text.
    /// </summary>
    /// <param name="text">The text to estimate tokens for.</param>
    /// <returns>Estimated number of tokens.</returns>
    int EstimateTokens(string text);

    /// <summary>
    /// Estimates the total number of tokens for multiple text segments.
    /// </summary>
    /// <param name="texts">Collection of text segments to estimate tokens for.</param>
    /// <returns>Estimated total number of tokens across all segments.</returns>
    int EstimateTokens(IEnumerable<string> texts);
}
