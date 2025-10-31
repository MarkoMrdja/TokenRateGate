using Azure.AI.OpenAI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TokenRateGate.Abstractions;
using TokenRateGate.Core;

namespace TokenRateGate.Azure;

/// <summary>
/// Extension methods for wrapping Azure OpenAI ChatClient with rate limiting capabilities.
/// Provides a fluent API for creating rate-limited Azure chat clients.
/// </summary>
public static class AzureChatClientRateLimitExtensions
{
    /// <summary>
    /// Wraps an Azure OpenAI client with rate limiting using the specified ITokenRateGate instance.
    /// </summary>
    /// <param name="azureClient">The Azure OpenAI client to wrap</param>
    /// <param name="rateGate">The rate gate instance to use for rate limiting</param>
    /// <param name="deploymentName">The Azure deployment name (e.g., "my-gpt-4-deployment")</param>
    /// <param name="modelName">The underlying model name for token estimation (e.g., "gpt-4", "gpt-35-turbo")</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics</param>
    /// <returns>A rate-limited Azure chat client wrapper</returns>
    /// <exception cref="ArgumentNullException">Thrown when azureClient or rateGate is null</exception>
    /// <exception cref="ArgumentException">Thrown when deploymentName or modelName is null or whitespace</exception>
    /// <remarks>
    /// This extension method creates an IRateLimitedAzureChatClient wrapper that automatically:
    /// - Estimates input and output tokens using OpenAI's tiktoken
    /// - Reserves capacity before making API calls
    /// - Executes Azure OpenAI chat completions (both streaming and non-streaming)
    /// - Records actual token usage
    /// - Releases reservations
    ///
    /// Azure OpenAI uses deployment names instead of model names for API calls, but requires
    /// the underlying model name for accurate token estimation.
    ///
    /// Example usage:
    /// <code>
    /// var rateGate = serviceProvider.GetRequiredService&lt;ITokenRateGate&gt;();
    /// var azureClient = new AzureOpenAIClient(endpoint, credential);
    /// var rateLimitedClient = azureClient.WithRateLimit(rateGate, "my-gpt-4-deployment", "gpt-4");
    ///
    /// var messages = new[] { new UserChatMessage("Hello!") };
    /// var response = await rateLimitedClient.CompleteChatAsync(messages);
    /// </code>
    /// </remarks>
    public static IRateLimitedAzureChatClient WithRateLimit(
        this AzureOpenAIClient azureClient,
        ITokenRateGate rateGate,
        string deploymentName,
        string modelName,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(azureClient);
        ArgumentNullException.ThrowIfNull(rateGate);
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("Deployment name cannot be null or whitespace", nameof(deploymentName));
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        return new RateLimitedAzureChatClient(azureClient, rateGate, deploymentName, modelName, loggerFactory);
    }

    /// <summary>
    /// Wraps an Azure OpenAI client with rate limiting using a named rate gate from the service provider.
    /// The rate gate is resolved from the current IServiceProvider using TokenRateGateServiceAccessor.
    /// </summary>
    /// <param name="azureClient">The Azure OpenAI client to wrap</param>
    /// <param name="gateName">The name of the rate gate to resolve from DI. Pass null for the default rate gate.</param>
    /// <param name="deploymentName">The Azure deployment name (e.g., "my-gpt-4-deployment")</param>
    /// <param name="modelName">The underlying model name for token estimation (e.g., "gpt-4", "gpt-35-turbo")</param>
    /// <returns>A rate-limited Azure chat client wrapper</returns>
    /// <exception cref="ArgumentNullException">Thrown when azureClient is null</exception>
    /// <exception cref="ArgumentException">Thrown when deploymentName or modelName is null or whitespace</exception>
    /// <exception cref="InvalidOperationException">Thrown when TokenRateGateServiceAccessor is not initialized or the named rate gate is not registered</exception>
    /// <remarks>
    /// This overload automatically resolves the rate gate from dependency injection using the specified name.
    /// Use this when you have multiple rate gates registered with different names (e.g., for different tenants or rate limits).
    /// For using the default rate gate, prefer the simpler overload without the gateName parameter.
    ///
    /// Example usage:
    /// <code>
    /// // Using named rate gates for multi-tenant scenarios
    /// var premiumClient = new AzureOpenAIClient(endpoint, credential);
    /// var premiumRateLimitedClient = premiumClient.WithRateLimit("premium", "premium-gpt-4-deployment", "gpt-4");
    ///
    /// var basicClient = new AzureOpenAIClient(endpoint, credential);
    /// var basicRateLimitedClient = basicClient.WithRateLimit("basic", "basic-gpt-35-deployment", "gpt-35-turbo");
    /// </code>
    /// </remarks>
    public static IRateLimitedAzureChatClient WithRateLimit(
        this AzureOpenAIClient azureClient,
        string? gateName,
        string deploymentName,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(azureClient);
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("Deployment name cannot be null or whitespace", nameof(deploymentName));
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

        return new RateLimitedAzureChatClient(azureClient, rateGate, deploymentName, modelName, loggerFactory);
    }

    /// <summary>
    /// Wraps an Azure OpenAI client with rate limiting using the default rate gate from the service provider.
    /// This is a convenience overload that uses the default rate gate.
    /// </summary>
    /// <param name="azureClient">The Azure OpenAI client to wrap</param>
    /// <param name="deploymentName">The Azure deployment name (e.g., "my-gpt-4-deployment")</param>
    /// <param name="modelName">The underlying model name for token estimation (e.g., "gpt-4", "gpt-35-turbo")</param>
    /// <returns>A rate-limited Azure chat client wrapper</returns>
    /// <exception cref="ArgumentNullException">Thrown when azureClient is null</exception>
    /// <exception cref="ArgumentException">Thrown when deploymentName or modelName is null or whitespace</exception>
    /// <exception cref="InvalidOperationException">Thrown when TokenRateGateServiceAccessor is not initialized</exception>
    /// <remarks>
    /// This is equivalent to calling WithRateLimit(null, deploymentName, modelName).
    ///
    /// Example usage:
    /// <code>
    /// var azureClient = new AzureOpenAIClient(endpoint, credential);
    /// var rateLimitedClient = azureClient.WithRateLimit("my-gpt-4-deployment", "gpt-4");
    ///
    /// var messages = new[] { new UserChatMessage("Hello!") };
    /// var response = await rateLimitedClient.CompleteChatAsync(messages);
    /// </code>
    /// </remarks>
    public static IRateLimitedAzureChatClient WithRateLimit(
        this AzureOpenAIClient azureClient,
        string deploymentName,
        string modelName)
    {
        return azureClient.WithRateLimit(gateName: null, deploymentName: deploymentName, modelName: modelName);
    }
}
