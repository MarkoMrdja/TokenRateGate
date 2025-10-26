using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using TokenRateGate.Extensions.DependencyInjection;

namespace DependencyInjectionSample;

/// <summary>
/// This sample demonstrates TokenRateGate integration with dependency injection.
/// It shows how to register TokenRateGate with IServiceCollection and use it in services.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== TokenRateGate Dependency Injection Sample ===\n");

        // Build a host with dependency injection
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Register TokenRateGate with default configuration
                services.AddTokenRateGate(options =>
                {
                    options.TokenLimit = 100_000;
                    options.WindowSeconds = 60;
                    options.MaxConcurrentRequests = 5;
                    options.SafetyBufferPercentage = 0.10; // 10% safety buffer
                });

                // Register our service that uses TokenRateGate
                services.AddTransient<LlmService>();
            })
            .Build();

        // Get the service from DI container
        var llmService = host.Services.GetRequiredService<LlmService>();

        // Use the service
        await llmService.ProcessRequestsAsync();

        Console.WriteLine("\n=== Sample completed ===");
    }
}

/// <summary>
/// Example service that uses TokenRateGate via dependency injection
/// </summary>
public class LlmService
{
    private readonly ITokenRateGate _rateGate;
    private readonly ILogger<LlmService> _logger;

    public LlmService(ITokenRateGate rateGate, ILogger<LlmService> logger)
    {
        _rateGate = rateGate;
        _logger = logger;
    }

    public async Task ProcessRequestsAsync()
    {
        Console.WriteLine("Processing multiple LLM requests with rate limiting...\n");

        // Simulate processing 5 requests
        for (int i = 1; i <= 5; i++)
        {
            await ProcessSingleRequestAsync(i);
            await Task.Delay(500); // Small delay between requests
        }

        // Show final stats
        var stats = _rateGate.GetUsageStats();
        Console.WriteLine("\nFinal Statistics:");
        Console.WriteLine($"  Total capacity used: {stats.UsagePercentage:P1}");
        Console.WriteLine($"  Waiting requests: {stats.WaitingRequestsCount}");
    }

    private async Task ProcessSingleRequestAsync(int requestNumber)
    {
        Console.WriteLine($"Request {requestNumber}: Starting...");

        // Estimate tokens for this request
        var inputTokens = Random.Shared.Next(1000, 3000);
        var estimatedOutputTokens = Random.Shared.Next(500, 1500);

        _logger.LogInformation(
            "Request {RequestNumber}: Requesting {InputTokens} input + {OutputTokens} output tokens",
            requestNumber, inputTokens, estimatedOutputTokens);

        // Reserve tokens using the injected rate gate
        await using var reservation = await _rateGate.ReserveTokensAsync(
            inputTokens,
            estimatedOutputTokens);

        Console.WriteLine($"Request {requestNumber}: ✓ Reservation granted ({reservation.ReservedTokens} tokens)");

        // Simulate LLM API call
        await Task.Delay(Random.Shared.Next(100, 300));

        // Simulate actual usage (usually close to estimate, 90-110% of reserved)
        var actualTokens = (int)(reservation.ReservedTokens * (0.9 + Random.Shared.NextDouble() * 0.2));
        var actualInput = inputTokens;
        var actualOutput = Math.Max(0, actualTokens - actualInput);
        reservation.RecordActualUsage(actualInput, actualOutput);

        Console.WriteLine($"Request {requestNumber}: Completed (actual: {actualTokens} tokens)\n");
    }
}
