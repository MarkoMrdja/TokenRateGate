using OpenAI.Chat;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Wrapper interface for OpenAI ChatClient that provides rate-limited chat completion methods.
/// This interface enables cleaner dependency injection and testing scenarios.
/// </summary>
public interface IRateLimitedChatClient
{
    /// <summary>
    /// Executes a chat completion with automatic rate limiting.
    /// </summary>
    /// <param name="messages">The chat messages to send</param>
    /// <param name="options">Optional chat completion options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The chat completion response</returns>
    Task<ChatCompletion> CompleteChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a chat completion with streaming and automatic rate limiting.
    /// </summary>
    /// <param name="messages">The chat messages to send</param>
    /// <param name="options">Optional chat completion options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An async enumerable of streaming chat completion updates</returns>
    IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteChatStreamingAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the underlying ChatClient instance.
    /// </summary>
    ChatClient Client { get; }

    /// <summary>
    /// Gets the model name this client is configured for.
    /// </summary>
    string ModelName { get; }
}
