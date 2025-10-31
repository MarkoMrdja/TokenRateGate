using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using TokenRateGate.Abstractions;
using TokenRateGate.Core;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Extension methods for wrapping ChatClient with rate limiting capabilities.
/// Provides a fluent API for creating rate-limited chat clients.
/// </summary>
public static class ChatClientRateLimitExtensions
{
    /// <summary>
    /// Wraps a ChatClient with rate limiting using the specified ITokenRateGate instance.
    /// </summary>
    /// <param name="client">The OpenAI ChatClient to wrap</param>
    /// <param name="rateGate">The rate gate instance to use for rate limiting</param>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo") for token estimation</param>
    /// <returns>A rate-limited chat client wrapper</returns>
    /// <exception cref="ArgumentNullException">Thrown when client or rateGate is null</exception>
    /// <exception cref="ArgumentException">Thrown when modelName is null or whitespace</exception>
    /// <remarks>
    /// This extension method creates an IRateLimitedChatClient wrapper that automatically:
    /// - Estimates input and output tokens using OpenAI's tiktoken
    /// - Reserves capacity before making API calls
    /// - Executes chat completions (both streaming and non-streaming)
    /// - Records actual token usage
    /// - Releases reservations
    ///
    /// Example usage:
    /// <code>
    /// var rateGate = serviceProvider.GetRequiredService&lt;ITokenRateGate&gt;();
    /// var client = new ChatClient("gpt-4", apiKey);
    /// var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4");
    ///
    /// var messages = new[] { new UserChatMessage("Hello!") };
    /// var response = await rateLimitedClient.CompleteChatAsync(messages);
    /// </code>
    /// </remarks>
    public static IRateLimitedChatClient WithRateLimit(
        this ChatClient client,
        ITokenRateGate rateGate,
        string modelName,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(rateGate);
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        return new RateLimitedChatClient(client, rateGate, modelName, loggerFactory);
    }

    /// <summary>
    /// Wraps a ChatClient with rate limiting using a named rate gate from the service provider.
    /// The rate gate is resolved from the current IServiceProvider using TokenRateGateServiceAccessor.
    /// </summary>
    /// <param name="client">The OpenAI ChatClient to wrap</param>
    /// <param name="gateName">The name of the rate gate to resolve from DI. Pass null for the default rate gate.</param>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo") for token estimation</param>
    /// <returns>A rate-limited chat client wrapper</returns>
    /// <exception cref="ArgumentNullException">Thrown when client is null</exception>
    /// <exception cref="ArgumentException">Thrown when modelName is null or whitespace</exception>
    /// <exception cref="InvalidOperationException">Thrown when TokenRateGateServiceAccessor is not initialized or the named rate gate is not registered</exception>
    /// <remarks>
    /// This overload automatically resolves the rate gate from dependency injection using the specified name.
    /// Use this when you have multiple rate gates registered with different names (e.g., for different tenants or rate limits).
    /// For using the default rate gate, prefer the simpler overload without the gateName parameter.
    ///
    /// Example usage:
    /// <code>
    /// // Using named rate gates for multi-tenant scenarios
    /// var premiumClient = new ChatClient("gpt-4", apiKey);
    /// var premiumRateLimitedClient = premiumClient.WithRateLimit("premium", "gpt-4");
    ///
    /// var basicClient = new ChatClient("gpt-3.5-turbo", apiKey);
    /// var basicRateLimitedClient = basicClient.WithRateLimit("basic", "gpt-3.5-turbo");
    /// </code>
    /// </remarks>
    public static IRateLimitedChatClient WithRateLimit(
        this ChatClient client,
        string? gateName,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        // Resolve the rate gate from service provider
        ITokenRateGate rateGate;
        ILoggerFactory? loggerFactory;

        if (string.IsNullOrWhiteSpace(gateName))
        {
            // Get default rate gate
            rateGate = TokenRateGateServiceAccessor.GetRequiredService<ITokenRateGate>();
            loggerFactory = TokenRateGateServiceAccessor.GetService<ILoggerFactory>();
        }
        else
        {
            // Get named rate gate using ITokenRateGateFactory
            var factory = TokenRateGateServiceAccessor.GetRequiredService<ITokenRateGateFactory>();
            rateGate = factory.GetOrCreate(gateName);
            loggerFactory = TokenRateGateServiceAccessor.GetService<ILoggerFactory>();
        }

        return new RateLimitedChatClient(client, rateGate, modelName, loggerFactory);
    }

    /// <summary>
    /// Wraps a ChatClient with rate limiting using the default rate gate from the service provider.
    /// This is a convenience overload that uses the default rate gate.
    /// </summary>
    /// <param name="client">The OpenAI ChatClient to wrap</param>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo") for token estimation</param>
    /// <returns>A rate-limited chat client wrapper</returns>
    /// <exception cref="ArgumentNullException">Thrown when client is null</exception>
    /// <exception cref="ArgumentException">Thrown when modelName is null or whitespace</exception>
    /// <exception cref="InvalidOperationException">Thrown when TokenRateGateServiceAccessor is not initialized</exception>
    /// <remarks>
    /// This is equivalent to calling WithRateLimit(null, modelName).
    ///
    /// Example usage:
    /// <code>
    /// var client = new ChatClient("gpt-4", apiKey);
    /// var rateLimitedClient = client.WithRateLimit("gpt-4");
    ///
    /// var messages = new[] { new UserChatMessage("Hello!") };
    /// var response = await rateLimitedClient.CompleteChatAsync(messages);
    /// </code>
    /// </remarks>
    public static IRateLimitedChatClient WithRateLimit(
        this ChatClient client,
        string modelName)
    {
        return client.WithRateLimit(gateName: null, modelName: modelName);
    }
}
