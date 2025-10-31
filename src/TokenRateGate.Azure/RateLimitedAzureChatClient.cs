using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using TokenRateGate.Abstractions;

namespace TokenRateGate.Azure;

/// <summary>
/// Wrapper implementation for Azure OpenAI ChatClient that provides rate-limited chat completion methods.
/// This class delegates to the internal TokenRateGate extension methods for actual rate limiting logic.
/// </summary>
internal class RateLimitedAzureChatClient : IRateLimitedAzureChatClient
{
    private readonly AzureOpenAIClient _azureClient;
    private readonly ChatClient _chatClient;
    private readonly string _deploymentName;
    private readonly string _modelName;
    private readonly ITokenRateGate _rateGate;
    private readonly AzureChatHelper _helper;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new rate-limited Azure chat client wrapper.
    /// </summary>
    /// <param name="azureClient">The underlying Azure OpenAI client</param>
    /// <param name="rateGate">The rate gate instance to use for rate limiting</param>
    /// <param name="deploymentName">The Azure deployment name</param>
    /// <param name="modelName">The model name for token estimation</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics</param>
    /// <exception cref="ArgumentNullException">Thrown when azureClient or rateGate is null</exception>
    /// <exception cref="ArgumentException">Thrown when deploymentName or modelName is null or whitespace</exception>
    public RateLimitedAzureChatClient(
        AzureOpenAIClient azureClient,
        ITokenRateGate rateGate,
        string deploymentName,
        string modelName,
        ILoggerFactory? loggerFactory = null)
    {
        _azureClient = azureClient ?? throw new ArgumentNullException(nameof(azureClient));
        _rateGate = rateGate ?? throw new ArgumentNullException(nameof(rateGate));

        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("Deployment name cannot be null or whitespace", nameof(deploymentName));
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        _deploymentName = deploymentName;
        _modelName = modelName;
        _logger = loggerFactory?.CreateLogger<RateLimitedAzureChatClient>();

        // Get the chat client for this deployment
        _chatClient = azureClient.GetChatClient(deploymentName);

        // Create helper for token estimation and usage extraction
        var helperLogger = loggerFactory?.CreateLogger<AzureChatHelper>();
        _helper = new AzureChatHelper(deploymentName, modelName, options: null, logger: helperLogger);
    }

    /// <summary>
    /// Gets the underlying Azure OpenAI client instance.
    /// </summary>
    public AzureOpenAIClient Client => _azureClient;

    /// <summary>
    /// Gets the deployment name this client is configured for.
    /// </summary>
    public string DeploymentName => _deploymentName;

    /// <summary>
    /// Gets the model name used for token estimation.
    /// </summary>
    public string ModelName => _modelName;

    /// <summary>
    /// Executes an Azure OpenAI chat completion with automatic rate limiting.
    /// </summary>
    /// <param name="messages">The chat messages to send</param>
    /// <param name="options">Optional chat completion options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The chat completion response</returns>
    public Task<ChatCompletion> CompleteChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _rateGate.ExecuteAzureChatAsync(
            _chatClient,
            messages,
            _helper,
            options,
            _logger,
            cancellationToken);
    }

    /// <summary>
    /// Executes an Azure OpenAI chat completion with streaming and automatic rate limiting.
    /// </summary>
    /// <param name="messages">The chat messages to send</param>
    /// <param name="options">Optional chat completion options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An async enumerable of streaming chat completion updates</returns>
    public IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteChatStreamingAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _rateGate.ExecuteAzureChatStreamingAsync(
            _chatClient,
            messages,
            _helper,
            options,
            _logger,
            cancellationToken);
    }
}
