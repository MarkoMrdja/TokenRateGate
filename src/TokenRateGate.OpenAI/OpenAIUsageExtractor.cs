using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using TokenRateGate.Abstractions;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Extracts actual token usage information from OpenAI chat completion responses.
/// This enables accurate tracking and efficiency measurement by comparing
/// actual usage against estimated tokens.
/// </summary>
public class OpenAIUsageExtractor : ITokenUsageExtractor<ChatCompletion>
{
    private readonly ILogger<OpenAIUsageExtractor> _logger;

    /// <summary>
    /// Creates a new OpenAI usage extractor.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics</param>
    public OpenAIUsageExtractor(ILogger<OpenAIUsageExtractor>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIUsageExtractor>.Instance;
    }

    /// <inheritdoc />
    public int? ExtractInputTokens(ChatCompletion response)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            var usage = response.Usage;
            if (usage == null)
            {
                _logger.LogWarning("Chat completion response does not contain usage information");
                return null;
            }

            // ChatTokenUsage uses PromptTokenCount for input tokens
            var inputTokens = usage.InputTokenCount;

            _logger.LogDebug(
                "Extracted {InputTokens} input tokens from completion {CompletionId}",
                inputTokens,
                response.Id);

            return inputTokens;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting input tokens from response");
            return null;
        }
    }

    /// <inheritdoc />
    public int? ExtractOutputTokens(ChatCompletion response)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            var usage = response.Usage;
            if (usage == null)
            {
                _logger.LogWarning("Chat completion response does not contain usage information");
                return null;
            }

            // ChatTokenUsage uses CompletionTokenCount for output tokens
            var outputTokens = usage.OutputTokenCount;

            _logger.LogDebug(
                "Extracted {OutputTokens} output tokens from completion {CompletionId}",
                outputTokens,
                response.Id);

            return outputTokens;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting output tokens from response");
            return null;
        }
    }

    /// <inheritdoc />
    public int? ExtractTotalTokens(ChatCompletion response)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            var usage = response.Usage;
            if (usage == null)
            {
                _logger.LogWarning("Chat completion response does not contain usage information");
                return null;
            }

            var totalTokens = usage.TotalTokenCount;

            _logger.LogDebug(
                "Extracted {TotalTokens} total tokens from completion {CompletionId} (Input: {InputTokens}, Output: {OutputTokens})",
                totalTokens,
                response.Id,
                usage.InputTokenCount,
                usage.OutputTokenCount);

            return totalTokens;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting total tokens from response");

            // Try fallback: sum input + output
            var inputTokens = ExtractInputTokens(response);
            var outputTokens = ExtractOutputTokens(response);

            if (inputTokens.HasValue && outputTokens.HasValue)
            {
                var fallbackTotal = inputTokens.Value + outputTokens.Value;
                _logger.LogInformation("Using fallback total calculation: {Total} tokens", fallbackTotal);
                return fallbackTotal;
            }

            return null;
        }
    }
}
