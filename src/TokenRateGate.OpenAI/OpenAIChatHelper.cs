using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using TokenRateGate.Core.Options;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Helper class for integrating OpenAI ChatClient with TokenRateGate.
/// Encapsulates token estimation and usage extraction logic for OpenAI chat completions.
/// This class is designed to be reused across multiple API calls to avoid redundant configuration.
/// </summary>
public class OpenAIChatHelper
{
    private readonly string _modelName;
    private readonly OpenAITokenEstimator _estimator;
    private readonly OpenAIUsageExtractor _extractor;
    private readonly ILogger<OpenAIChatHelper> _logger;

    /// <summary>
    /// Creates a new OpenAI chat helper for the specified model.
    /// </summary>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo", "gpt-4o")</param>
    /// <param name="options">Optional TokenRateGate options for output token estimation strategy</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <exception cref="ArgumentException">Thrown when modelName is null or whitespace</exception>
    public OpenAIChatHelper(
        string modelName,
        TokenRateGateOptions? options = null,
        ILogger<OpenAIChatHelper>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        _modelName = modelName;
        _logger = logger ?? NullLogger<OpenAIChatHelper>.Instance;

        // Create options wrapper if not provided
        var optionsValue = options ?? new TokenRateGateOptions();
        var optionsWrapper = Options.Create(optionsValue);

        // Create estimator with model-specific tiktoken encoding
        var estimatorLogger = (ILogger<OpenAITokenEstimator>?)logger ?? NullLogger<OpenAITokenEstimator>.Instance;
        _estimator = new OpenAITokenEstimator(modelName, optionsWrapper, estimatorLogger);

        // Create usage extractor
        var extractorLogger = (ILogger<OpenAIUsageExtractor>?)logger ?? NullLogger<OpenAIUsageExtractor>.Instance;
        _extractor = new OpenAIUsageExtractor(extractorLogger);

        _logger.LogInformation(
            "Created OpenAI chat helper for model {ModelName}",
            _modelName);
    }

    /// <summary>
    /// Gets the model name this helper is configured for.
    /// </summary>
    public string ModelName => _modelName;

    /// <summary>
    /// Estimates the number of input tokens for a chat completion request.
    /// Uses tiktoken encoding specific to the configured model.
    /// </summary>
    /// <param name="messages">The chat messages to estimate tokens for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The estimated number of input tokens</returns>
    internal Task<int> EstimateInputTokensAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        return _estimator.EstimateInputTokensAsync(messages, cancellationToken);
    }

    /// <summary>
    /// Estimates the number of output tokens for a chat completion request.
    /// Uses the configured output estimation strategy (FixedMultiplier, FixedAmount, or Conservative).
    /// </summary>
    /// <param name="messages">The chat messages to estimate output tokens for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The estimated number of output tokens</returns>
    internal Task<int> EstimateOutputTokensAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        return _estimator.EstimateOutputTokensAsync(messages, cancellationToken);
    }

    /// <summary>
    /// Extracts actual token usage from an OpenAI chat completion response.
    /// Returns null values if usage information is not available in the response.
    /// </summary>
    /// <param name="response">The chat completion response</param>
    /// <returns>A tuple containing (input tokens, output tokens), with null values if unavailable</returns>
    internal (int? inputTokens, int? outputTokens) ExtractUsage(ChatCompletion response)
    {
        var inputTokens = _extractor.ExtractInputTokens(response);
        var outputTokens = _extractor.ExtractOutputTokens(response);
        return (inputTokens, outputTokens);
    }

    /// <summary>
    /// Extracts actual token usage from a streaming chat completion update.
    /// Streaming responses typically only include usage information in the final chunk.
    /// </summary>
    /// <param name="update">The streaming chat completion update</param>
    /// <returns>A tuple containing (input tokens, output tokens), with null values if unavailable in this chunk</returns>
    internal (int? inputTokens, int? outputTokens) ExtractStreamingUsage(StreamingChatCompletionUpdate update)
    {
        // Usage information is typically only available in the final streaming chunk
        if (update.Usage != null)
        {
            return (update.Usage.InputTokenCount, update.Usage.OutputTokenCount);
        }

        return (null, null);
    }
}
