# TokenRateGate

A .NET library for intelligent token-based rate limiting of LLM API requests. Prevents exceeding provider limits (tokens-per-minute and requests-per-minute) by managing token reservations, queuing requests, and tracking actual vs. estimated usage.

## Features

- **Token-Based Rate Limiting**: Track and limit by token usage, not just request count
- **Intelligent Queuing**: Automatically queues requests when capacity is exhausted
- **Accurate Tracking**: Records actual token usage from API responses for precise accounting
- **Multiple Providers**: Built-in support for OpenAI and Azure OpenAI
- **Multi-Tenant**: Factory pattern for managing different rate limits per tenant/model
- **Real-Time Monitoring**: Detailed usage statistics and capacity tracking
- **Dependency Injection**: First-class DI support with configuration binding
- **High Performance**: Optimized for throughput with minimal overhead
- **Thread-Safe**: Concurrent request handling with proper synchronization

## Installation

```bash
# Core rate limiting
dotnet add package TokenRateGate.Core
dotnet add package TokenRateGate.Extensions.DependencyInjection

# For OpenAI integration
dotnet add package TokenRateGate.OpenAI

# For Azure OpenAI integration
dotnet add package TokenRateGate.Azure
```

## Quick Start

### 1. Dependency Injection Setup (Recommended)

The easiest way to use TokenRateGate is with dependency injection:

```csharp
using TokenRateGate.Extensions.DependencyInjection;

// In your Program.cs or Startup.cs
var builder = WebApplication.CreateBuilder(args);

// Register TokenRateGate with configuration
builder.Services.AddTokenRateGate(options =>
{
    options.TokenLimit = 500000;        // 500K tokens per minute
    options.WindowSeconds = 60;
    options.MaxConcurrentRequests = 10  // Limit concurrent API calls
});

var app = builder.Build();
```

Or bind from configuration:

```json
// appsettings.json
{
  "TokenRateGate": {
    "TokenLimit": 500000,
    "WindowSeconds": 60,
    "MaxConcurrentRequests": 10
  }
}
```

```csharp
builder.Services.AddTokenRateGate(
    builder.Configuration.GetSection("TokenRateGate"));
```

### 2. Using TokenRateGate Without Client Libraries

Use the core rate limiting directly in your services:

```csharp
public class MyLlmService
{
    private readonly ITokenRateGate _rateGate;
    private readonly ILogger<MyLlmService> _logger;

    public MyLlmService(ITokenRateGate rateGate, ILogger<MyLlmService> logger)
    {
        _rateGate = rateGate;
        _logger = logger;
    }

    public async Task<string> CallLlmAsync(string prompt, int estimatedOutputTokens = 1000)
    {
        // Estimate input tokens (simplified - use proper tokenizer in production)
        int inputTokens = prompt.Length / 4;

        // Reserve capacity before calling the LLM
        await using var reservation = await _rateGate.ReserveTokensAsync(
            inputTokens,
            estimatedOutputTokens);

        _logger.LogInformation("Reserved {Tokens} tokens", reservation.ReservedTokens);

        // Make your LLM API call here
        var response = await CallYourLlmApiAsync(prompt);

        // Record actual usage from the response
        var actualInputTokens = response.Usage.InputTokens;
        var actualOutputTokens = response.Usage.OutputTokens;
        reservation.RecordActualUsage(actualInputTokens, actualOutputTokens);

        _logger.LogInformation(
            "Actual usage: {Input} input + {Output} output tokens",
            actualInputTokens,
            actualOutputTokens);

        return response.Content;
    }
}
```

### 3. Using with OpenAI SDK

TokenRateGate integrates seamlessly with the OpenAI SDK:

```csharp
using OpenAI.Chat;
using TokenRateGate.OpenAI;
using TokenRateGate.Abstractions;

public class ChatService
{
    private readonly ITokenRateGate _rateGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _apiKey;

    public ChatService(
        ITokenRateGate rateGate,
        ILoggerFactory loggerFactory,
        IConfiguration configuration)
    {
        _rateGate = rateGate;
        _loggerFactory = loggerFactory;
        _apiKey = configuration["OpenAI:ApiKey"];
    }

    public async Task<string> AskQuestionAsync(string question)
    {
        // Create OpenAI client and wrap with rate limiting
        var client = new ChatClient("gpt-4", _apiKey);
        var rateLimitedClient = client.WithRateLimit(_rateGate, "gpt-4", _loggerFactory);

        // Make rate-limited API call - automatic token tracking!
        var messages = new[] { new UserChatMessage(question) };
        var response = await rateLimitedClient.CompleteChatAsync(messages);

        return response.Content[0].Text;
    }

    public async Task<string> AskQuestionStreamingAsync(string question)
    {
        var client = new ChatClient("gpt-4", _apiKey);
        var rateLimitedClient = client.WithRateLimit(_rateGate, "gpt-4", _loggerFactory);

        var messages = new[] { new UserChatMessage(question) };
        var result = new StringBuilder();

        // Streaming support with automatic token tracking
        await foreach (var chunk in rateLimitedClient.CompleteChatStreamingAsync(messages))
        {
            if (chunk.ContentUpdate.Count > 0)
            {
                var text = chunk.ContentUpdate[0].Text;
                result.Append(text);
                Console.Write(text);
            }
        }

        return result.ToString();
    }
}
```

### 4. Using with Azure OpenAI

```csharp
using Azure;
using Azure.AI.OpenAI;
using TokenRateGate.Azure;

public class AzureChatService
{
    private readonly ITokenRateGate _rateGate;
    private readonly ILoggerFactory _loggerFactory;

    public AzureChatService(ITokenRateGate rateGate, ILoggerFactory loggerFactory)
    {
        _rateGate = rateGate;
        _loggerFactory = loggerFactory;
    }

    public async Task<string> AskQuestionAsync(string question)
    {
        var azureClient = new AzureOpenAIClient(
            new Uri("https://your-resource.openai.azure.com/"),
            new AzureKeyCredential("your-api-key"));

        // Wrap with rate limiting (deployment name + model name for token counting)
        var rateLimitedClient = azureClient.WithRateLimit(
            _rateGate,
            deploymentName: "my-gpt4-deployment",
            modelName: "gpt-4",
            _loggerFactory);

        var messages = new[] { new UserChatMessage(question) };
        var response = await rateLimitedClient.CompleteChatAsync(messages);

        return response.Content[0].Text;
    }
}
```

## Multi-Tenant Configuration

Support different rate limits for different users, models, or tenants:

```csharp
// Registration in Program.cs
builder.Services.AddTokenRateGateFactory();

builder.Services.AddNamedTokenRateGate("basic-tier", options =>
{
    options.TokenLimit = 100000;  // 100K tokens/min for basic users
    options.WindowSeconds = 60;
});

builder.Services.AddNamedTokenRateGate("premium-tier", options =>
{
    options.TokenLimit = 1000000; // 1M tokens/min for premium users
    options.WindowSeconds = 60;
});

// Usage in your service
public class MultiTenantChatService
{
    private readonly ITokenRateGateFactory _factory;
    private readonly ILoggerFactory _loggerFactory;

    public MultiTenantChatService(
        ITokenRateGateFactory factory,
        ILoggerFactory loggerFactory)
    {
        _factory = factory;
        _loggerFactory = loggerFactory;
    }

    public async Task<string> AskQuestionAsync(string question, string tier)
    {
        // Get rate gate for the tenant's tier
        var rateGate = _factory.GetOrCreate(tier);

        var client = new ChatClient("gpt-4", "your-api-key");
        var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4", _loggerFactory);

        var messages = new[] { new UserChatMessage(question) };
        var response = await rateLimitedClient.CompleteChatAsync(messages);

        return response.Content[0].Text;
    }
}
```

## Standalone Usage (Without DI)

You can also use TokenRateGate without dependency injection:

```csharp
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.OpenAI;
using Microsoft.Extensions.Logging;

// Create rate gate manually
var options = new TokenRateGateOptions
{
    TokenLimit = 500000,
    WindowSeconds = 60,
    MaxConcurrentRequests = 10
};

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
});

var rateGate = new TokenRateGate.Core.TokenRateGate(options, loggerFactory);

// Use with OpenAI
var client = new ChatClient("gpt-4", "your-api-key");
var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4", loggerFactory);

var messages = new[] { new UserChatMessage("Hello!") };
var response = await rateLimitedClient.CompleteChatAsync(messages);
Console.WriteLine(response.Content[0].Text);
```

## Monitoring Usage

```csharp
public class MonitoringService
{
    private readonly ITokenRateGate _rateGate;

    public MonitoringService(ITokenRateGate rateGate)
    {
        _rateGate = rateGate;
    }

    public void LogCurrentUsage()
    {
        var stats = _rateGate.GetUsageStats();

        Console.WriteLine($"Current Usage: {stats.CurrentUsage}/{stats.EffectiveCapacity} tokens");
        Console.WriteLine($"Reserved: {stats.ReservedTokens} tokens");
        Console.WriteLine($"Available: {stats.AvailableTokens} tokens");
        Console.WriteLine($"Usage: {stats.UsagePercentage:F1}%");
        Console.WriteLine($"Near Capacity: {stats.IsNearCapacity}");
    }
}
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `TokenLimit` | 500000 | Maximum tokens per window |
| `WindowSeconds` | 60 | Time window in seconds |
| `SafetyBufferPercentage` | 0.05 (5%) | Percentage of TokenLimit reserved as safety buffer |
| `MaxConcurrentRequests` | 1000 | Maximum concurrent API requests |
| `MaxRequestsPerMinute` | int.MaxValue | RPM limit (in addition to token limit) |
| `MaxWaitTime` | 2 minutes | Maximum time to wait when queued |
| `OutputEstimationStrategy` | FixedMultiplier | How to estimate output tokens |
| `OutputMultiplier` | 0.5 | Multiplier for FixedMultiplier strategy |
| `DefaultOutputTokens` | 1000 | Fixed output for FixedAmount strategy |

### Output Estimation Strategies

- **FixedMultiplier**: Multiply input tokens by `OutputMultiplier` (default 0.5)
- **FixedAmount**: Add a fixed `DefaultOutputTokens` (default 1000)
- **Conservative**: Assume output = input (reserve 2x input tokens)

## How It Works

1. **Token Estimation**: Before making an API call, TokenRateGate estimates input and output tokens using tiktoken
2. **Capacity Check**: Checks if estimated tokens fit within the current limit
3. **Reservation**: Reserves capacity for the request (blocks if capacity unavailable)
4. **API Call**: Executes the LLM API call
5. **Actual Usage**: Extracts actual token usage from the response
6. **Recording**: Records actual usage and releases the reservation
7. **Sliding Window**: Old usage automatically expires after the configured window

## Advanced Topics

### Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddTokenRateGate(name: "tokenrategate", tags: ["rate-limiting"]);
```

### Custom Token Estimation

```csharp
// Configure estimation strategy
builder.Services.AddTokenRateGate(options =>
{
    options.OutputEstimationStrategy = OutputEstimationStrategy.Conservative;
    // Now reserves 2x input tokens (assumes output = input)
});
```

### Logging

TokenRateGate provides detailed structured logging:

```csharp
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Debug);  // See detailed token tracking
});
```

## Samples

Check the [samples/](samples/) directory for complete examples:

- **OpenAIIntegration**: Basic OpenAI usage, streaming, monitoring
- **AzureOpenAI.BasicUsage**: Azure OpenAI integration
- More samples available in the repository

## Performance

- **Minimal Overhead**: Token estimation uses efficient tiktoken library
- **Optimized Queuing**: Fast capacity checks with double-check locking
- **High Throughput**: Achieves >95% capacity utilization under load
- **Concurrent Requests**: Supports high concurrency with proper synchronization

See [tests/TokenRateGate.PerformanceTests](tests/TokenRateGate.PerformanceTests) for benchmarks.

## Testing

```bash
# Run all tests
dotnet test

# Run specific test categories
dotnet test --filter "Category=Integration"
dotnet test --filter "Category=Performance"
```

## Requirements

- .NET 6.0, 8.0, or 9.0
- OpenAI SDK (for OpenAI integration): `OpenAI` NuGet package
- Azure OpenAI SDK (for Azure integration): `Azure.AI.OpenAI` NuGet package

## Packages

- **TokenRateGate.Core**: Core rate limiting engine
- **TokenRateGate.Abstractions**: Interfaces and abstractions
- **TokenRateGate.OpenAI**: OpenAI SDK integration
- **TokenRateGate.Azure**: Azure OpenAI SDK integration
- **TokenRateGate.Extensions.DependencyInjection**: DI extensions and factory

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Submit a pull request

## License

[Your License Here]

## Acknowledgments

- Uses [tiktoken](https://github.com/openai/tiktoken) for accurate token counting
- Built for the [OpenAI SDK for .NET](https://github.com/openai/openai-dotnet)
- Supports [Azure OpenAI SDK](https://github.com/Azure/azure-sdk-for-net)
