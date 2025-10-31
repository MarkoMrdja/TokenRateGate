using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using TokenRateGate.Core.Options;
using TokenRateGate.OpenAI;

namespace TokenRateGate.Azure;

/// <summary>
/// Helper class for integrating Azure OpenAI ChatClient with TokenRateGate.
/// Encapsulates token estimation and usage extraction logic for Azure OpenAI chat completions.
/// This class is designed to be reused across multiple API calls to avoid redundant configuration.
/// </summary>
/// <remarks>
/// Azure OpenAI uses deployment names instead of model names. The helper needs to know both:
/// - Deployment name: Used for API calls to Azure OpenAI
/// - Model name: Used for tiktoken encoding selection (e.g., "gpt-4", "gpt-35-turbo")
/// </remarks>
internal class AzureChatHelper
{
    private readonly string _deploymentName;
    private readonly string _modelName;
    private readonly OpenAITokenEstimator _estimator;
    private readonly OpenAIUsageExtractor _extractor;
    private readonly ILogger<AzureChatHelper> _logger;

    /// <summary>
    /// Creates a new Azure OpenAI chat helper for the specified deployment and model.
    /// </summary>
    /// <param name="deploymentName">The Azure OpenAI deployment name (e.g., "gpt-4-deployment")</param>
    /// <param name="modelName">The underlying model name for token counting (e.g., "gpt-4", "gpt-35-turbo", "gpt-4o")</param>
    /// <param name="options">Optional TokenRateGate options for output token estimation strategy</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <exception cref="ArgumentException">Thrown when deploymentName or modelName is null or whitespace</exception>
    public AzureChatHelper(
        string deploymentName,
        string modelName,
        TokenRateGateOptions? options = null,
        ILogger<AzureChatHelper>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("Deployment name cannot be null or whitespace", nameof(deploymentName));
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        _deploymentName = deploymentName;
        _modelName = modelName;
        _logger = logger ?? NullLogger<AzureChatHelper>.Instance;

        // Create options wrapper if not provided
        var optionsValue = options ?? new TokenRateGateOptions();
        var optionsWrapper = Options.Create(optionsValue);

        // Create estimator with model-specific tiktoken encoding (uses NullLogger internally)
        // We use the OpenAI token estimator since Azure OpenAI uses the same models
        _estimator = new OpenAITokenEstimator(modelName, optionsWrapper);

        // Create usage extractor (uses NullLogger internally) - same as OpenAI since response format is identical
        _extractor = new OpenAIUsageExtractor();

        _logger.LogInformation(
            "Created Azure OpenAI chat helper for deployment {DeploymentName} using model {ModelName}",
            _deploymentName,
            _modelName);
    }

    /// <summary>
    /// Gets the deployment name this helper is configured for.
    /// </summary>
    public string DeploymentName => _deploymentName;

    /// <summary>
    /// Gets the model name this helper uses for token estimation.
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
    /// Extracts actual token usage from an Azure OpenAI chat completion response.
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
