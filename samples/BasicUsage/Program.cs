using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Utils;

namespace BasicUsage;

/// <summary>
/// This sample demonstrates basic TokenRateGate usage without dependency injection.
/// It shows manual integration with explicit resource management.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // Setup logging to see what's happening
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        Console.WriteLine("=== TokenRateGate Basic Usage Sample ===\n");

        // Example 1: Simple usage with default settings
        await Example1_SimpleUsage(loggerFactory);

        // Example 2: Configuration with custom limits
        await Example2_CustomConfiguration(loggerFactory);

        // Example 3: Handling queue wait times
        await Example3_QueueBehavior(loggerFactory);

        // Example 4: Recording actual usage for accuracy
        await Example4_ActualUsageTracking(loggerFactory);

        // Example 5: Monitoring usage statistics
        await Example5_MonitoringStats(loggerFactory);

        Console.WriteLine("\n=== All examples completed ===");
    }

    /// <summary>
    /// Example 1: Simple usage with default configuration
    /// </summary>
    static async Task Example1_SimpleUsage(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 1: Simple Usage ---");

        // Configure rate limiter for 100,000 tokens per minute
        var options = new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 3
        };

        // Create the rate gate
        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        // Reserve tokens for a simulated LLM request
        // Estimating 1000 input tokens + 500 output tokens
        var inputTokens = 1000;
        var estimatedOutputTokens = 500;

        Console.WriteLine($"Requesting reservation for {inputTokens} input + {estimatedOutputTokens} output tokens...");

        // Reserve tokens - this will wait if capacity is not available
        await using var reservation = await rateGate.ReserveTokensAsync(
            inputTokens,
            estimatedOutputTokens,
            CancellationToken.None
        );

        Console.WriteLine($"✓ Reservation granted: {reservation.Id}");
        Console.WriteLine($"  Reserved tokens: {reservation.ReservedTokens}");

        // Simulate LLM API call
        await Task.Delay(100);

        // After getting response, record actual usage (in real scenario, get from API response)
        var actualInputTokens = 1000;
        var actualOutputTokens = 450; // Slightly less than estimated
        reservation.RecordActualUsage(actualInputTokens, actualOutputTokens);

        var actualTotal = actualInputTokens + actualOutputTokens;
        Console.WriteLine($"  Actual usage recorded: {actualTotal} tokens");
        Console.WriteLine($"  Estimation efficiency: {(double)actualTotal / (inputTokens + estimatedOutputTokens):P1}\n");
    }

    /// <summary>
    /// Example 2: Custom configuration with aggressive limits
    /// </summary>
    static async Task Example2_CustomConfiguration(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 2: Custom Configuration ---");

        // Configure with tighter limits and safety buffer
        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,              // 50K tokens per window
            WindowSeconds = 30,               // 30 second window
            SafetyBufferPercentage = 0.10,    // Reserve 10% (5K) tokens as buffer
            MaxConcurrentRequests = 2,        // Only 2 concurrent requests
            MaxRequestsPerMinute = 10,        // Also limit to 10 requests/minute
            RequestWindowSeconds = 60
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        Console.WriteLine("Configuration:");
        Console.WriteLine($"  Token limit: {options.TokenLimit:N0} tokens per {options.WindowSeconds}s");
        Console.WriteLine($"  Safety buffer: {options.SafetyBufferPercentage:P0} ({(int)(options.TokenLimit * options.SafetyBufferPercentage):N0} tokens)");
        Console.WriteLine($"  Max concurrent: {options.MaxConcurrentRequests}");
        Console.WriteLine($"  Request limit: {options.MaxRequestsPerMinute} per minute");

        // Make a request
        await using var reservation = await rateGate.ReserveTokensAsync(1500, 500);
        Console.WriteLine($"✓ Reservation granted with effective limit of {options.TokenLimit - (int)(options.TokenLimit * options.SafetyBufferPercentage):N0} tokens\n");
    }

    /// <summary>
    /// Example 3: Queue behavior when capacity is exhausted
    /// </summary>
    static async Task Example3_QueueBehavior(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 3: Queue Behavior ---");

        // Configure with very low limit to demonstrate queuing
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 10,
            MaxConcurrentRequests = 1,  // Force sequential processing
            MaxWaitTime = TimeSpan.FromSeconds(30)
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        Console.WriteLine("Submitting 3 requests that exceed capacity...");

        // Submit multiple requests concurrently
        var tasks = new List<Task>();

        for (int i = 1; i <= 3; i++)
        {
            int requestNumber = i;
            tasks.Add(Task.Run(async () =>
            {
                var startTime = DateTime.UtcNow;
                Console.WriteLine($"  Request {requestNumber}: Requesting 4000 tokens...");

                await using var reservation = await rateGate.ReserveTokensAsync(2500, 1500);
                var waitTime = DateTime.UtcNow - startTime;

                Console.WriteLine($"  Request {requestNumber}: ✓ Granted after {waitTime.TotalMilliseconds:F0}ms wait");

                // Simulate work
                await Task.Delay(2000);
                reservation.RecordActualUsage(2500, 1300);
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("All requests completed\n");
    }

    /// <summary>
    /// Example 4: Recording actual usage for better accuracy
    /// </summary>
    static async Task Example4_ActualUsageTracking(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 4: Actual Usage Tracking ---");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5  // Estimate output as 50% of input
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        Console.WriteLine("Making request with estimated vs actual tracking...\n");

        // Estimate tokens (input + estimated output)
        var inputTokens = 2000;
        var estimatedOutput = (int)(inputTokens * 0.5);
        var estimatedTotal = inputTokens + estimatedOutput;

        Console.WriteLine($"  Input tokens: {inputTokens}");
        Console.WriteLine($"  Estimated output: {estimatedOutput}");
        Console.WriteLine($"  Estimated total: {estimatedTotal}");

        await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutput);

        // Simulate API call and get actual response
        await Task.Delay(100);

        // In real scenario, extract actual tokens from API response
        var actualInputTokens = 2000;
        var actualOutputTokens = 1200;  // More than estimated!
        var actualTotal = actualInputTokens + actualOutputTokens;

        Console.WriteLine($"\n  Actual input: {actualInputTokens}");
        Console.WriteLine($"  Actual output: {actualOutputTokens}");
        Console.WriteLine($"  Actual total: {actualTotal}");

        // Record actual usage - this is important for accuracy!
        reservation.RecordActualUsage(actualInputTokens, actualOutputTokens);

        var efficiency = (double)actualTotal / estimatedTotal;
        Console.WriteLine($"\n  Reservation efficiency: {efficiency:P1}");

        if (efficiency > 1.0)
        {
            Console.WriteLine("  ⚠ Under-estimated! Consider adjusting estimation strategy.");
        }
        else if (efficiency < 0.8)
        {
            Console.WriteLine("  ⚠ Over-estimated! Wasting capacity. Consider adjusting estimation strategy.");
        }
        else
        {
            Console.WriteLine("  ✓ Good estimation accuracy!");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Example 5: Monitoring usage statistics
    /// </summary>
    static async Task Example5_MonitoringStats(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 5: Monitoring Statistics ---");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,
            WindowSeconds = 60
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        // Get initial stats
        var stats = rateGate.GetUsageStats();
        Console.WriteLine("Initial state:");
        PrintStats(stats);

        // Make some requests
        Console.WriteLine("\nMaking 3 requests...");

        {
            await using var r1 = await rateGate.ReserveTokensAsync(6_000, 4_000);
            await using var r2 = await rateGate.ReserveTokensAsync(9_000, 6_000);
            await using var r3 = await rateGate.ReserveTokensAsync(5_000, 3_000);

            stats = rateGate.GetUsageStats();
            Console.WriteLine("\nWith 3 active reservations:");
            PrintStats(stats);

            // Simulate work
            await Task.Delay(100);

            // Record actual usage
            r1.RecordActualUsage(6_000, 3_500);
            r2.RecordActualUsage(9_000, 5_000);
            r3.RecordActualUsage(5_000, 2_800);
        }

        // Check stats after completion
        await Task.Delay(100);  // Give time for cleanup
        stats = rateGate.GetUsageStats();
        Console.WriteLine("\nAfter requests completed:");
        PrintStats(stats);

        Console.WriteLine();
    }

    static void PrintStats(TokenUsageStats stats)
    {
        Console.WriteLine($"  Current usage: {stats.CurrentUsage:N0} / {stats.EffectiveCapacity:N0} tokens");
        Console.WriteLine($"  Reserved: {stats.TotalReserved:N0} tokens");
        Console.WriteLine($"  Available: {stats.AvailableTokens:N0} tokens");
        Console.WriteLine($"  Usage: {stats.UsagePercentage:F1}%");
        Console.WriteLine($"  Active reservations: {stats.ActiveReservationsCount}");
        Console.WriteLine($"  Waiting requests: {stats.WaitingRequestsCount}");

        if (stats.IsNearCapacity)
        {
            Console.WriteLine("  ⚠ Near capacity!");
        }
    }
}
