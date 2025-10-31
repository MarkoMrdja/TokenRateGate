using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using TokenRateGate.Abstractions;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Wrapper implementation for OpenAI ChatClient that provides rate-limited chat completion methods.
/// This class delegates to the internal TokenRateGate extension methods for actual rate limiting logic.
/// </summary>
internal class RateLimitedChatClient : IRateLimitedChatClient
{
    private readonly ChatClient _client;
    private readonly string _modelName;
    private readonly ITokenRateGate _rateGate;
    private readonly OpenAIChatHelper _helper;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new rate-limited chat client wrapper.
    /// </summary>
    /// <param name="client">The underlying OpenAI ChatClient</param>
    /// <param name="rateGate">The rate gate instance to use for rate limiting</param>
    /// <param name="modelName">The model name for token estimation</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics</param>
    /// <exception cref="ArgumentNullException">Thrown when client or rateGate is null</exception>
    /// <exception cref="ArgumentException">Thrown when modelName is null or whitespace</exception>
    public RateLimitedChatClient(
        ChatClient client,
        ITokenRateGate rateGate,
        string modelName,
        ILoggerFactory? loggerFactory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _rateGate = rateGate ?? throw new ArgumentNullException(nameof(rateGate));

        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        _modelName = modelName;
        _logger = loggerFactory?.CreateLogger<RateLimitedChatClient>();

        // Create helper for token estimation and usage extraction
        var helperLogger = loggerFactory?.CreateLogger<OpenAIChatHelper>();
        _helper = new OpenAIChatHelper(modelName, options: null, logger: helperLogger);
    }

    /// <summary>
    /// Gets the underlying ChatClient instance.
    /// </summary>
    public ChatClient Client => _client;

    /// <summary>
    /// Gets the model name this client is configured for.
    /// </summary>
    public string ModelName => _modelName;

    /// <summary>
    /// Executes a chat completion with automatic rate limiting.
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
        return _rateGate.ExecuteChatAsync(
            _client,
            messages,
            _helper,
            options,
            _logger,
            cancellationToken);
    }

    /// <summary>
    /// Executes a chat completion with streaming and automatic rate limiting.
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
        return _rateGate.ExecuteChatStreamingAsync(
            _client,
            messages,
            _helper,
            options,
            _logger,
            cancellationToken);
    }
}
