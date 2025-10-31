using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace TokenRateGate.Azure;

/// <summary>
/// Wrapper interface for Azure OpenAI ChatClient that provides rate-limited chat completion methods.
/// This interface enables cleaner dependency injection and testing scenarios for Azure OpenAI.
/// </summary>
public interface IRateLimitedAzureChatClient
{
    /// <summary>
    /// Executes an Azure OpenAI chat completion with automatic rate limiting.
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
    /// Executes an Azure OpenAI chat completion with streaming and automatic rate limiting.
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
    /// Gets the underlying Azure OpenAI ChatClient instance.
    /// </summary>
    AzureOpenAIClient Client { get; }

    /// <summary>
    /// Gets the deployment name this client is configured for.
    /// </summary>
    string DeploymentName { get; }

    /// <summary>
    /// Gets the model name used for token estimation.
    /// </summary>
    string ModelName { get; }
}
