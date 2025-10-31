using TokenRateGate.Core.Abstractions;

namespace TokenRateGate.Core.TokenEstimation;

/// <summary>
/// A simple character-based token estimator using a fixed character-to-token ratio.
/// This is a general-purpose estimator that works across different models and providers.
/// </summary>
/// <remarks>
/// The default ratio of 4 characters per token is based on empirical observations across
/// various LLM tokenizers (GPT-3.5, GPT-4, Claude, etc.). This provides a reasonable
/// approximation for most English text. For more precise estimation, consider using
/// provider-specific token counters (e.g., tiktoken for OpenAI models).
/// </remarks>
public class CharacterBasedTokenEstimator : ITokenEstimator
{
    private readonly double _charactersPerToken;

    /// <summary>
    /// Creates a new character-based token estimator with the default ratio of 4 characters per token.
    /// </summary>
    public CharacterBasedTokenEstimator()
        : this(charactersPerToken: 4.0)
    {
    }

    /// <summary>
    /// Creates a new character-based token estimator with a custom character-to-token ratio.
    /// </summary>
    /// <param name="charactersPerToken">
    /// The number of characters that typically correspond to one token.
    /// Common values: 3.5-4.5 for English text, may vary for other languages.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when charactersPerToken is less than or equal to zero.</exception>
    public CharacterBasedTokenEstimator(double charactersPerToken)
    {
        if (charactersPerToken <= 0)
            throw new ArgumentException("Characters per token must be greater than zero", nameof(charactersPerToken));

        _charactersPerToken = charactersPerToken;
    }

    /// <inheritdoc />
    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return (int)Math.Ceiling(text.Length / _charactersPerToken);
    }

    /// <inheritdoc />
    public int EstimateTokens(IEnumerable<string> texts)
    {
        if (texts == null)
            return 0;

        int totalCharacters = 0;
        foreach (var text in texts)
        {
            if (!string.IsNullOrEmpty(text))
                totalCharacters += text.Length;
        }

        return (int)Math.Ceiling(totalCharacters / _charactersPerToken);
    }
}
