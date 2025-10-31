using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using TokenRateGate.Azure;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace AzureOpenAI.BasicUsage;

/// <summary>
/// Sample application demonstrating basic usage of TokenRateGate with Azure OpenAI.
/// This example shows how to:
/// 1. Configure Azure OpenAI client with API key or managed identity
/// 2. Set up TokenRateGate with Azure-specific rate limits
/// 3. Execute rate-limited chat completions
/// 4. Monitor token usage and efficiency
///
/// Configuration options (in priority order):
/// 1. Environment variables: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, etc.
/// 2. appsettings.Development.json: { "AzureOpenAI": { "Endpoint": "...", "ApiKey": "..." } }
/// 3. appsettings.json: { "AzureOpenAI": { "Endpoint": "...", "ApiKey": "..." } }
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // Build configuration from appsettings.json and environment variables
        var basePath = Directory.GetCurrentDirectory();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // Configure logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("=== Azure OpenAI Basic Usage Sample ===\n");

        // Read configuration (environment variables take precedence)
        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"]
            ?? configuration["AzureOpenAI:Endpoint"]
            ?? "https://YOUR-RESOURCE-NAME.openai.azure.com/";
        var apiKey = configuration["AZURE_OPENAI_API_KEY"]
            ?? configuration["AzureOpenAI:ApiKey"];
        var deploymentName = configuration["AZURE_OPENAI_DEPLOYMENT"]
            ?? configuration["AzureOpenAI:DeploymentName"]
            ?? "gpt-4";
        var modelName = configuration["AZURE_OPENAI_MODEL"]
            ?? configuration["AzureOpenAI:ModelName"]
            ?? "gpt-4"; // The underlying model for token counting

        // Azure OpenAI rate limits (example values - adjust for your deployment)
        // Check your Azure Portal for actual limits
        var tokenLimit = int.TryParse(configuration["RateLimits:TokenLimit"], out var tl) ? tl : 90_000;
        var requestLimit = int.TryParse(configuration["RateLimits:RequestLimit"], out var rl) ? rl : 300;

        // Validate configuration
        var isPlaceholderEndpoint = endpoint.Contains("YOUR-RESOURCE-NAME");
        var isPlaceholderKey = string.IsNullOrWhiteSpace(apiKey) || apiKey.Equals("your-api-key-here", StringComparison.OrdinalIgnoreCase);

        if (isPlaceholderKey && isPlaceholderEndpoint)
        {
            logger.LogWarning("⚠️  Azure OpenAI not configured properly.");
            logger.LogWarning("Please update your configuration using one of these methods:");
            logger.LogWarning("  1. Environment variables:");
            logger.LogWarning("     export AZURE_OPENAI_ENDPOINT=\"https://YOUR-RESOURCE.openai.azure.com/\"");
            logger.LogWarning("     export AZURE_OPENAI_API_KEY=\"your-api-key\"");
            logger.LogWarning("     export AZURE_OPENAI_DEPLOYMENT=\"gpt-4\"");
            logger.LogWarning("  2. appsettings.Development.json:");
            logger.LogWarning("     {{");
            logger.LogWarning("       \"AzureOpenAI\": {{");
            logger.LogWarning("         \"Endpoint\": \"https://YOUR-RESOURCE.openai.azure.com/\",");
            logger.LogWarning("         \"ApiKey\": \"your-actual-api-key\",");
            logger.LogWarning("         \"DeploymentName\": \"gpt-4\"");
            logger.LogWarning("       }}");
            logger.LogWarning("     }}\n");
            logger.LogInformation("Exiting - please configure Azure OpenAI credentials.");
            return;
        }

        logger.LogInformation("Configuration:");
        logger.LogInformation("  Endpoint: {Endpoint}", endpoint);
        logger.LogInformation("  Deployment: {Deployment}", deploymentName);
        logger.LogInformation("  Model: {Model}", modelName);
        logger.LogInformation("  Rate Limits: {TokenLimit} tokens/min, {RequestLimit} requests/min",
            tokenLimit, requestLimit);
        logger.LogInformation("  Auth: {Auth}\n", string.IsNullOrWhiteSpace(apiKey) ? "Managed Identity" : "API Key");

        // Step 1: Create Azure OpenAI client
        AzureOpenAIClient azureClient;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Option A: Use API key authentication
            logger.LogInformation("Using API key authentication\n");
            azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));
        }
        else
        {
            // Option B: Use managed identity (recommended for production)
            logger.LogInformation("Using DefaultAzureCredential (managed identity)\n");
            azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new DefaultAzureCredential());
        }

        // Step 2: Configure TokenRateGate
        var options = new TokenRateGateOptions
        {
            TokenLimit = tokenLimit,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.05, // Reserve 5% as safety margin
            MaxConcurrentRequests = 10,
            MaxRequestsPerMinute = requestLimit,
            RequestWindowSeconds = 60,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5, // Assume output is ~50% of input
            MaxWaitTime = TimeSpan.FromMinutes(2)
        };

        var rateGate = new TokenRateGate.Core.TokenRateGate(
            Microsoft.Extensions.Options.Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>());

        // Step 3: Wrap Azure OpenAI client with rate limiting
        var rateLimitedClient = azureClient.WithRateLimit(
            rateGate,
            deploymentName,
            modelName,
            loggerFactory);

        logger.LogInformation("TokenRateGate configured and ready\n");

        // Step 4: Execute rate-limited chat completions
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a helpful AI assistant."),
            new UserChatMessage("Explain what Azure OpenAI is in 2-3 sentences.")
        };

        logger.LogInformation("Sending chat completion request...\n");

        try
        {
            // Execute the chat completion with automatic rate limiting
            var response = await rateLimitedClient.CompleteChatAsync(messages);

            logger.LogInformation("Response from Azure OpenAI:");
            logger.LogInformation("{Response}\n", response.Content[0].Text);

            // Step 5: Monitor usage stats
            var stats = rateGate.GetUsageStats();
            logger.LogInformation("Usage Statistics:");
            logger.LogInformation("  Current Usage: {CurrentUsage} tokens", stats.CurrentUsage);
            logger.LogInformation("  Reserved Tokens: {ReservedTokens}", stats.TotalReserved);
            logger.LogInformation("  Available Capacity: {AvailableCapacity} tokens", stats.AvailableCapacity);
            logger.LogInformation("  Usage Percentage: {UsagePercentage:F1}%", stats.UsagePercentage);
            logger.LogInformation("  Active Reservations: {ActiveReservations}", stats.ActiveReservationsCount);

            // Step 6: Example of streaming chat completion
            logger.LogInformation("\n=== Streaming Example ===\n");

            var streamingMessages = new List<ChatMessage>
            {
                new SystemChatMessage("You are a helpful AI assistant."),
                new UserChatMessage("Count from 1 to 5.")
            };

            logger.LogInformation("Streaming response: ");
            await foreach (var update in rateLimitedClient.CompleteChatStreamingAsync(streamingMessages))
            {
                foreach (var contentPart in update.ContentUpdate)
                {
                    Console.Write(contentPart.Text);
                }
            }

            logger.LogInformation("\n\nStreaming completed successfully!");

            // Final stats
            var finalStats = rateGate.GetUsageStats();
            logger.LogInformation("\nFinal Statistics:");
            logger.LogInformation("  Total Usage: {CurrentUsage} tokens", finalStats.CurrentUsage);
            logger.LogInformation("  Active Requests: {ActiveReservations}", finalStats.ActiveReservationsCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing chat completion");
            throw;
        }

        logger.LogInformation("\n=== Sample Completed ===");
    }
}
