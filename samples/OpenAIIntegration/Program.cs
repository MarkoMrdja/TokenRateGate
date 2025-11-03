using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;
using TokenRateGate.OpenAI;

namespace OpenAIIntegration;

/// <summary>
/// Sample application demonstrating TokenRateGate integration with OpenAI SDK.
///
/// IMPORTANT: This sample requires an OpenAI API key to run.
///
/// Configuration options (in priority order):
/// 1. Environment variable: OPENAI_API_KEY=sk-...
/// 2. appsettings.Development.json: { "OpenAI": { "ApiKey": "sk-..." } }
/// 3. appsettings.json: { "OpenAI": { "ApiKey": "sk-..." } }
///
/// If no API key is set, the sample will run in demonstration mode with mock examples.
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

        Console.WriteLine("=== TokenRateGate + OpenAI Integration Sample ===\n");

        // Try to get API key from multiple sources (environment variable takes precedence)
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? configuration["OpenAI:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("⚠️  No OpenAI API key found.");
            Console.WriteLine("Running in DEMONSTRATION MODE - no actual API calls will be made.\n");
            Console.WriteLine("To run with real OpenAI API, set your API key using one of these methods:");
            Console.WriteLine("  1. Environment variable:");
            Console.WriteLine("     export OPENAI_API_KEY=\"sk-...\"  # On Unix/Mac");
            Console.WriteLine("     set OPENAI_API_KEY=sk-...       # On Windows");
            Console.WriteLine("  2. appsettings.Development.json:");
            Console.WriteLine("     { \"OpenAI\": { \"ApiKey\": \"sk-...\" } }\n");

            await RunDemonstrationMode();
        }
        else
        {
            Console.WriteLine("✓ OpenAI API key detected.");
            Console.WriteLine("Running with REAL API CALLS.\n");

            await RunWithRealAPI(apiKey);
        }
    }

    /// <summary>
    /// Demonstrates the TokenRateGate + OpenAI integration patterns without making real API calls.
    /// </summary>
    static async Task RunDemonstrationMode()
    {
        Console.WriteLine("=== Example 1: Basic Setup ===\n");
        DemonstrateBasicSetup();

        Console.WriteLine("\n=== Example 2: Configuration Options ===\n");
        DemonstrateConfigurationOptions();

        Console.WriteLine("\n=== Example 3: Streaming Setup ===\n");
        DemonstrateStreamingSetup();

        Console.WriteLine("\n=== Example 4: Usage Monitoring ===\n");
        DemonstrateUsageMonitoring();

        Console.WriteLine("\nDemonstration complete!");
        Console.WriteLine("Set OPENAI_API_KEY to run with real API calls.");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Shows the most basic way to set up TokenRateGate with OpenAI.
    /// </summary>
    static void DemonstrateBasicSetup()
    {
        Console.WriteLine("// Step 1: Configure rate limiting options");
        Console.WriteLine("var options = new TokenRateGateOptions");
        Console.WriteLine("{");
        Console.WriteLine("    TokenLimit = 100000,              // 100K tokens");
        Console.WriteLine("    WindowSeconds = 60,                // per minute");
        Console.WriteLine("    MaxConcurrentRequests = 5,         // max 5 parallel requests");
        Console.WriteLine("    OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,");
        Console.WriteLine("    OutputMultiplier = 0.5             // estimate 50% of input for output");
        Console.WriteLine("};");
        Console.WriteLine();
        Console.WriteLine("// Step 2: Create the rate gate");
        Console.WriteLine("var rateGate = new TokenRateGate(Options.Create(options), logger);");
        Console.WriteLine();
        Console.WriteLine("// Step 3: Create OpenAI client and wrap with rate limiting");
        Console.WriteLine("var client = new ChatClient(\"gpt-4\", \"YOUR_API_KEY\");");
        Console.WriteLine("var rateLimitedClient = client.WithRateLimit(rateGate, \"gpt-4\", loggerFactory);");
        Console.WriteLine();
        Console.WriteLine("// Step 4: Make rate-limited API calls");
        Console.WriteLine("var messages = new List<ChatMessage> { new UserChatMessage(\"Hello!\") };");
        Console.WriteLine("var response = await rateLimitedClient.CompleteChatAsync(messages);");
        Console.WriteLine("Console.WriteLine(response.Content[0].Text);");
    }

    /// <summary>
    /// Shows different configuration strategies for various use cases.
    /// </summary>
    static void DemonstrateConfigurationOptions()
    {
        Console.WriteLine("=== Conservative Configuration (Better for cost control) ===");
        Console.WriteLine("var conservativeOptions = new TokenRateGateOptions");
        Console.WriteLine("{");
        Console.WriteLine("    TokenLimit = 50000,");
        Console.WriteLine("    WindowSeconds = 60,");
        Console.WriteLine("    OutputEstimationStrategy = OutputEstimationStrategy.Conservative,");
        Console.WriteLine("    MaxConcurrentRequests = 3");
        Console.WriteLine("};");
        Console.WriteLine("// Pros: Won't exceed limits, good for strict budgets");
        Console.WriteLine("// Cons: May under-utilize available capacity\n");

        Console.WriteLine("=== Optimized Configuration (Better for throughput) ===");
        Console.WriteLine("var optimizedOptions = new TokenRateGateOptions");
        Console.WriteLine("{");
        Console.WriteLine("    TokenLimit = 100000,");
        Console.WriteLine("    WindowSeconds = 60,");
        Console.WriteLine("    OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,");
        Console.WriteLine("    OutputMultiplier = 0.6,");
        Console.WriteLine("    MaxConcurrentRequests = 10");
        Console.WriteLine("};");
        Console.WriteLine("// Pros: Better throughput, more efficient capacity utilization");
        Console.WriteLine("// Cons: Requires tuning the multiplier based on actual usage\n");

        Console.WriteLine("=== Fixed Output Configuration (For predictable responses) ===");
        Console.WriteLine("var fixedOutputOptions = new TokenRateGateOptions");
        Console.WriteLine("{");
        Console.WriteLine("    TokenLimit = 100000,");
        Console.WriteLine("    WindowSeconds = 60,");
        Console.WriteLine("    OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount,");
        Console.WriteLine("    DefaultOutputTokens = 1000,  // Each response ~1000 tokens");
        Console.WriteLine("    MaxConcurrentRequests = 5");
        Console.WriteLine("};");
        Console.WriteLine("// Pros: Predictable reservation sizes, good for structured outputs");
        Console.WriteLine("// Cons: May waste capacity if actual output varies significantly");
    }

    /// <summary>
    /// Shows how to use streaming responses with rate limiting.
    /// </summary>
    static void DemonstrateStreamingSetup()
    {
        Console.WriteLine("// Streaming uses the same wrapper, different method:");
        Console.WriteLine();
        Console.WriteLine("await foreach (var update in rateLimitedClient.CompleteChatStreamingAsync(messages))");
        Console.WriteLine("{");
        Console.WriteLine("    if (update.ContentUpdate.Count > 0)");
        Console.WriteLine("        Console.Write(update.ContentUpdate[0].Text);");
        Console.WriteLine("}");
        Console.WriteLine();
        Console.WriteLine("// The rate gate automatically:");
        Console.WriteLine("// - Reserves tokens before streaming starts");
        Console.WriteLine("// - Holds the reservation during the entire stream");
        Console.WriteLine("// - Records actual usage from the final chunk");
        Console.WriteLine("// - Releases the reservation when done (even on error)");
    }

    /// <summary>
    /// Shows how to monitor token usage and capacity.
    /// </summary>
    static void DemonstrateUsageMonitoring()
    {
        Console.WriteLine("// Get current usage statistics:");
        Console.WriteLine("var stats = rateGate.GetUsageStats();");
        Console.WriteLine();
        Console.WriteLine("Console.WriteLine($\"Tokens Used: {stats.CurrentUsage}\");");
        Console.WriteLine("Console.WriteLine($\"Token Limit: {stats.TokenLimit}\");");
        Console.WriteLine("Console.WriteLine($\"Available: {stats.AvailableTokens}\");");
        Console.WriteLine("Console.WriteLine($\"Reserved: {stats.ActiveReservationsCount}\");");
        Console.WriteLine("Console.WriteLine($\"Waiting Requests: {stats.WaitingRequestsCount}\");");
        Console.WriteLine();
        Console.WriteLine("// Usage percentage:");
        Console.WriteLine("var usagePercent = (double)stats.CurrentUsage / stats.TokenLimit * 100;");
        Console.WriteLine("Console.WriteLine($\"Capacity: {usagePercent:F1}%\");");
    }

    /// <summary>
    /// Runs real examples using the OpenAI API.
    /// </summary>
    static async Task RunWithRealAPI(string apiKey)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole()
                   .SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>();

        Console.WriteLine("=== Example 1: Basic Chat Completion ===\n");
        await RunBasicChatExample(apiKey, logger);

        Console.WriteLine("\n=== Example 2: Streaming Chat Response ===\n");
        await RunStreamingExample(apiKey, logger);

        Console.WriteLine("\n=== Example 3: Parallel Requests ===\n");
        await RunParallelExample(apiKey, logger);

        Console.WriteLine("\n=== Example 4: Usage Monitoring ===\n");
        await RunMonitoringExample(apiKey, logger);

        Console.WriteLine("\nAll examples completed!");
    }

    static async Task RunBasicChatExample(string apiKey, ILogger<TokenRateGate.Core.TokenRateGate> logger)
    {
        // Configure rate limiting (OpenAI GPT-4: 10K tokens/min for tier 1)
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 3,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        };

        var rateGate = new TokenRateGate.Core.TokenRateGate(Options.Create(options), logger);

        // Create OpenAI client and wrap with rate limiting
        var client = new ChatClient("gpt-4o-mini", apiKey); // Using mini for lower cost
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4o-mini", loggerFactory);

        var messages = new List<ChatMessage>
        {
            new UserChatMessage("What is the capital of France? Answer in one sentence.")
        };

        Console.WriteLine("Sending question to OpenAI with rate limiting...");

        try
        {
            var response = await rateLimitedClient.CompleteChatAsync(messages);
            Console.WriteLine($"\nResponse: {response.Content[0].Text}");

            var stats = rateGate.GetUsageStats();
            Console.WriteLine($"\nTokens used: {stats.CurrentUsage}/{stats.TokenLimit}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    static async Task RunStreamingExample(string apiKey, ILogger<TokenRateGate.Core.TokenRateGate> logger)
    {
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 3,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount,
            DefaultOutputTokens = 500
        };

        var rateGate = new TokenRateGate.Core.TokenRateGate(Options.Create(options), logger);

        // Create OpenAI client and wrap with rate limiting
        var client = new ChatClient("gpt-4o-mini", apiKey);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4o-mini", loggerFactory);

        var messages = new List<ChatMessage>
        {
            new UserChatMessage("Count from 1 to 5, one number per line.")
        };

        Console.WriteLine("Streaming response from OpenAI...\n");

        try
        {
            await foreach (var update in rateLimitedClient.CompleteChatStreamingAsync(messages))
            {
                if (update.ContentUpdate.Count > 0)
                {
                    Console.Write(update.ContentUpdate[0].Text);
                }
            }

            Console.WriteLine("\n\nStream complete!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    static async Task RunParallelExample(string apiKey, ILogger<TokenRateGate.Core.TokenRateGate> logger)
    {
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 3,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        };

        var rateGate = new TokenRateGate.Core.TokenRateGate(Options.Create(options), logger);

        // Create OpenAI client and wrap with rate limiting
        var client = new ChatClient("gpt-4o-mini", apiKey);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4o-mini", loggerFactory);

        var questions = new[]
        {
            "What is 2+2?",
            "What is the speed of light?",
            "What is the largest planet?"
        };

        Console.WriteLine("Sending 3 questions in parallel (with rate limiting)...\n");

        try
        {
            var tasks = questions.Select(async (question, index) =>
            {
                var messages = new List<ChatMessage> { new UserChatMessage(question) };
                var response = await rateLimitedClient.CompleteChatAsync(messages);
                return (index + 1, question, response.Content[0].Text);
            });

            var results = await Task.WhenAll(tasks);

            foreach (var (num, question, answer) in results)
            {
                Console.WriteLine($"{num}. Q: {question}");
                Console.WriteLine($"   A: {answer}\n");
            }

            var stats = rateGate.GetUsageStats();
            Console.WriteLine($"Total tokens used: {stats.CurrentUsage}/{stats.TokenLimit}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    static async Task RunMonitoringExample(string apiKey, ILogger<TokenRateGate.Core.TokenRateGate> logger)
    {
        var options = new TokenRateGateOptions
        {
            TokenLimit = 5000, // Small limit to demonstrate monitoring
            WindowSeconds = 60,
            MaxConcurrentRequests = 2,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        };

        var rateGate = new TokenRateGate.Core.TokenRateGate(Options.Create(options), logger);

        // Create OpenAI client and wrap with rate limiting
        var client = new ChatClient("gpt-4o-mini", apiKey);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4o-mini", loggerFactory);

        Console.WriteLine("Monitoring token usage across multiple requests...\n");

        for (int i = 1; i <= 3; i++)
        {
            var statsBefore = rateGate.GetUsageStats();
            Console.WriteLine($"Request {i} - Before:");
            Console.WriteLine($"  Tokens: {statsBefore.CurrentUsage}/{statsBefore.TokenLimit}");
            Console.WriteLine($"  Usage: {(double)statsBefore.CurrentUsage / statsBefore.TokenLimit * 100:F1}%");
            Console.WriteLine($"  Available: {statsBefore.AvailableTokens}");

            try
            {
                var messages = new List<ChatMessage>
                {
                    new UserChatMessage($"Tell me a one-sentence fact about number {i}.")
                };

                var response = await rateLimitedClient.CompleteChatAsync(messages);

                var statsAfter = rateGate.GetUsageStats();
                Console.WriteLine($"Request {i} - After:");
                Console.WriteLine($"  Response: {response.Content[0].Text}");
                Console.WriteLine($"  Tokens: {statsAfter.CurrentUsage}/{statsAfter.TokenLimit}");
                Console.WriteLine($"  Usage: {(double)statsAfter.CurrentUsage / statsAfter.TokenLimit * 100:F1}%\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.Message}\n");
            }
        }
    }
}
