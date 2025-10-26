using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Options;
using TokenRateGate.Extensions.DependencyInjection;


namespace MultiTenantSample;

/// <summary>
/// This sample demonstrates managing multiple TokenRateGate instances for different tenants or API keys.
/// Each tenant gets its own rate limit pool with independent tracking.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== TokenRateGate Multi-Tenant Sample ===\n");

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Register the factory for managing multiple rate gates
                services.AddTokenRateGateFactory();

                // Register our multi-tenant service
                services.AddSingleton<MultiTenantLlmService>();
            })
            .Build();

        var service = host.Services.GetRequiredService<MultiTenantLlmService>();
        await service.ProcessRequestsAsync();

        Console.WriteLine("\n=== Sample completed ===");
    }
}

public class MultiTenantLlmService
{
    private readonly TokenRateGateFactory _factory;
    private readonly ILogger<MultiTenantLlmService> _logger;

    public MultiTenantLlmService(TokenRateGateFactory factory, ILogger<MultiTenantLlmService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task ProcessRequestsAsync()
    {
        // Simulate 3 different tenants with different rate limits
        var tenants = new[]
        {
            new { Name = "TenantA", Limit = 50_000, Window = 60 },
            new { Name = "TenantB", Limit = 100_000, Window = 60 },
            new { Name = "TenantC", Limit = 25_000, Window = 60 }
        };

        // Create rate gates for each tenant
        var rateGates = new Dictionary<string, TokenRateGate.Core.TokenRateGate>();
        foreach (var tenant in tenants)
        {
            // In a real application, you'd configure different options per tenant using named options
            // For this sample, we'll just get or create gates with the tenant name
            // Use the interface type to avoid casting issues
            rateGates[tenant.Name] = (TokenRateGate.Core.TokenRateGate)_factory.GetOrCreate(tenant.Name);
            Console.WriteLine($"Created rate gate for {tenant.Name}: {tenant.Limit:N0} tokens/{tenant.Window}s");
        }

        Console.WriteLine("\nProcessing requests for each tenant...\n");

        // Simulate requests from different tenants
        var tasks = new List<Task>();
        for (int i = 0; i < 3; i++)
        {
            foreach (var tenant in tenants)
            {
                var tenantName = tenant.Name;
                tasks.Add(ProcessTenantRequestAsync(rateGates[tenantName], tenantName, i + 1));
            }
        }

        await Task.WhenAll(tasks);

        // Show final stats for each tenant
        Console.WriteLine("\nFinal Statistics:");
        foreach (var tenant in tenants)
        {
            var stats = rateGates[tenant.Name].GetUsageStats();
            Console.WriteLine($"  {tenant.Name}: {stats.UsagePercentage:P1} capacity used, {stats.WaitingRequestsCount} waiting");
        }
    }

    private async Task ProcessTenantRequestAsync(TokenRateGate.Core.TokenRateGate rateGate, string tenantName, int requestNum)
    {
        try
        {
            var inputTokens = Random.Shared.Next(2000, 5000);
            var outputTokens = Random.Shared.Next(1000, 3000);

            _logger.LogInformation("{Tenant} Request {Num}: Reserving {Total} tokens",
                tenantName, requestNum, inputTokens + outputTokens);

            await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, outputTokens);

            Console.WriteLine($"{tenantName} Request {requestNum}: ✓ Reserved {reservation.ReservedTokens} tokens");

            // Simulate work
            await Task.Delay(Random.Shared.Next(50, 150));

            var actualTotal = (int)(reservation.ReservedTokens * 0.95);
            var actualInput = inputTokens;
            var actualOutput = actualTotal - actualInput;
            reservation.RecordActualUsage(actualInput, actualOutput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Tenant} Request {Num} failed", tenantName, requestNum);
        }
    }
}
