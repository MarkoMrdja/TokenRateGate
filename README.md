# TokenRateGate

**Stop getting HTTP 429 "Rate limit exceeded" errors from Azure OpenAI, OpenAI, and Anthropic Claude APIs.**

TokenRateGate is a .NET library that prevents rate limit errors by intelligently managing your token and request budgets. It tracks both TPM (Tokens-Per-Minute) and RPM (Requests-Per-Minute) limits, automatically queues requests when capacity is full, and ensures you never hit the dreaded 429 error again.

## The Problem

Getting this error when calling LLM APIs?

```
HTTP 429: Too Many Requests
Rate limit is exceeded. Try again in X seconds.
```

This happens when you exceed your API provider's limits:
- **Azure OpenAI / Azure AI Foundry**: TPM (Tokens-Per-Minute) limits based on your deployment tier
- **OpenAI API**: Both TPM and RPM (Requests-Per-Minute) limits
- **Anthropic Claude**: Rate limits based on usage tier

TokenRateGate **prevents these errors** by managing your token budget and queueing requests before they hit the API.

## Features

- **Prevents HTTP 429 Errors**: Enforces TPM and RPM limits before making API calls
- **Dual Limiting**: Tracks both token usage (TPM) and request count (RPM) - whichever is more restrictive applies
- **Smart Queuing**: Automatically queues requests when capacity is full, with configurable timeouts
- **Safety Buffer**: Configurable buffer to avoid hitting exact limits (prevents edge-case 429s)
- **Accurate Tracking**: Records actual token usage from API responses for precise capacity management
- **Multiple Providers**: Built-in support for OpenAI and Azure OpenAI SDKs
- **Multi-Tenant**: Factory pattern for managing different rate limits per tenant/model/tier
- **Real-Time Monitoring**: Detailed usage statistics and capacity tracking
- **Dependency Injection**: First-class DI support with configuration binding
- **High Performance**: Optimized for throughput with minimal overhead
- **Thread-Safe**: Concurrent request handling with proper synchronization

## Installation

### Option 1: Complete Solution (Recommended)

For OpenAI or Azure OpenAI users:

```bash
dotnet add package TokenRateGate
```

**Includes:** Everything - base engine, DI, OpenAI integration, Azure integration.

### Option 2: Custom LLM Providers

For Anthropic Claude, Google Gemini, or custom APIs:

```bash
dotnet add package TokenRateGate.Base
```

**Includes:** Core engine, DI support, character-based token estimation.
**Use when:** Building custom integrations without OpenAI/Azure SDKs.

### Option 3: Add Integrations Individually

```bash
# Base + OpenAI only
dotnet add package TokenRateGate.Base
dotnet add package TokenRateGate.OpenAI

# Base + Azure only
dotnet add package TokenRateGate.Base
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
    options.TokenLimit = 150000;              // 150K tokens per minute (Azure Standard tier)
    options.WindowSeconds = 60;               // 60-second sliding window
    options.SafetyBufferPercentage = 0.05;    // 5% safety buffer (avoids hitting exact limit)
    options.MaxConcurrentRequests = 10;       // Limit concurrent API calls
    options.MaxRequestsPerMinute = 100;       // Optional: Also enforce RPM limit
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

### 2. Using with Custom LLM Providers

For APIs without built-in token estimation (Anthropic Claude, Google Gemini, custom LLMs):

```csharp
using TokenRateGate.Core;
using TokenRateGate.Core.TokenEstimation;
using TokenRateGate.Abstractions;

public class CustomLlmService
{
    private readonly ITokenRateGate _rateGate;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly ILogger<CustomLlmService> _logger;

    public CustomLlmService(ITokenRateGate rateGate, ILogger<CustomLlmService> logger)
    {
        _rateGate = rateGate;
        _logger = logger;

        // Use character-based estimation (4 chars ≈ 1 token for most LLMs)
        _tokenEstimator = new CharacterBasedTokenEstimator();
    }

    public async Task<string> CallCustomLlmAsync(string prompt)
    {
        // Estimate tokens using character-based estimator
        int estimatedInputTokens = _tokenEstimator.EstimateTokens(prompt);
        int estimatedOutputTokens = 1000; // Your estimated response size

        // Reserve capacity before calling the LLM
        await using var reservation = await _rateGate.ReserveTokensAsync(
            estimatedInputTokens,
            estimatedOutputTokens);

        _logger.LogInformation("Reserved {Tokens} tokens", reservation.ReservedTokens);

        // Make your custom LLM API call
        var response = await CallYourCustomApiAsync(prompt);

        // Record actual usage from the response (IMPORTANT for accurate tracking)
        var actualTotalTokens = response.Usage.TotalTokens;
        reservation.RecordActualUsage(actualTotalTokens);

        _logger.LogInformation("Actual usage: {Tokens} tokens", actualTotalTokens);

        return response.Content;
    }
}
```

**Using CharacterBasedTokenEstimator:**

```csharp
// Default: 4 characters per token
var estimator = new CharacterBasedTokenEstimator();
int tokens = estimator.EstimateTokens("Hello world!");  // ≈ 3 tokens

// Custom ratio for different languages
var chineseEstimator = new CharacterBasedTokenEstimator(charactersPerToken: 2.0);
int chineseTokens = chineseEstimator.EstimateTokens("你好世界");  // Better for non-Latin scripts

// Estimate multiple texts
var messages = new[] { "System prompt", "User message", "Assistant response" };
int totalTokens = estimator.EstimateTokens(messages);
```

### 3. Using with OpenAI SDK (Recommended for OpenAI Users)

TokenRateGate integrates seamlessly with the OpenAI SDK.

**Note:** Logging is optional. The `WithRateLimit()` extension method accepts an optional `ILoggerFactory` parameter for diagnostics. If not provided, logging is disabled.

```csharp
using OpenAI.Chat;
using TokenRateGate.OpenAI;
using TokenRateGate.Abstractions;

public class ChatService
{
    private readonly ITokenRateGate _rateGate;
    private readonly string _apiKey;

    public ChatService(
        ITokenRateGate rateGate,
        IConfiguration configuration)
    {
        _rateGate = rateGate;
        _apiKey = configuration["OpenAI:ApiKey"];
    }

    public async Task<string> AskQuestionAsync(string question)
    {
        // Create OpenAI client and wrap with rate limiting
        var client = new ChatClient("gpt-4", _apiKey);
        var rateLimitedClient = client.WithRateLimit(_rateGate, "gpt-4");

        // Make rate-limited API call - automatic token tracking!
        var messages = new[] { new UserChatMessage(question) };
        var response = await rateLimitedClient.CompleteChatAsync(messages);

        return response.Content[0].Text;
    }

    public async Task<string> AskQuestionStreamingAsync(string question)
    {
        var client = new ChatClient("gpt-4", _apiKey);
        var rateLimitedClient = client.WithRateLimit(_rateGate, "gpt-4");

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

### 4. Using with Azure OpenAI (Recommended for Azure Users)

**Note:** Logging is optional. The `WithRateLimit()` extension method accepts an optional `ILoggerFactory` parameter for diagnostics. If not provided, logging is disabled.

```csharp
using Azure;
using Azure.AI.OpenAI;
using TokenRateGate.Azure;

public class AzureChatService
{
    private readonly ITokenRateGate _rateGate;

    public AzureChatService(ITokenRateGate rateGate)
    {
        _rateGate = rateGate;
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
            modelName: "gpt-4");

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

    public MultiTenantChatService(ITokenRateGateFactory factory)
    {
        _factory = factory;
    }

    public async Task<string> AskQuestionAsync(string question, string tier)
    {
        // Get rate gate for the tenant's tier
        var rateGate = _factory.GetOrCreate(tier);

        var client = new ChatClient("gpt-4", "your-api-key");
        var rateLimitedClient = client.WithRateLimit(rateGate, "gpt-4");

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

var rateGate = new TokenRateGate(options, loggerFactory);

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
| `TokenLimit` | 500000 | Maximum tokens per window (TPM limit) |
| `WindowSeconds` | 60 | Time window in seconds for token tracking |
| `SafetyBufferPercentage` | 0.05 (5%) | Percentage of TokenLimit reserved as safety buffer<br>Effective limit = `TokenLimit * (1 - SafetyBufferPercentage)` |
| `MaxConcurrentRequests` | 1000 | Maximum concurrent active reservations |
| `MaxRequestsPerMinute` | null | Optional RPM limit (enforced in addition to token limit)<br>If both are configured, whichever is more restrictive applies |
| `RequestWindowSeconds` | 120 | Time window for RPM tracking (default: max(120s, 2×WindowSeconds)) |
| `MaxWaitTime` | 2 minutes | Maximum time a request waits in queue before timing out |
| `OutputEstimationStrategy` | FixedMultiplier | How to estimate output tokens when not provided |
| `OutputMultiplier` | 0.5 | Multiplier for FixedMultiplier strategy |
| `DefaultOutputTokens` | 1000 | Fixed output for FixedAmount strategy |

### Output Estimation Strategies

- **FixedMultiplier**: Multiply input tokens by `OutputMultiplier` (default 0.5)
- **FixedAmount**: Add a fixed `DefaultOutputTokens` (default 1000)
- **Conservative**: Assume output = input (reserve 2x input tokens)

## How It Works

TokenRateGate uses a **dual-component capacity system**:

**Current Capacity = Historical Usage + Active Reservations**

1. **Token Estimation**: Before making an API call, estimate input and output tokens
2. **Capacity Check**: Checks both:
   - **TPM Check**: `(Historical Usage + Active Reservations + Requested Tokens) <= (TokenLimit - SafetyBuffer)`
   - **RPM Check**: `Current Request Count < MaxRequestsPerMinute`
   - **Both must pass** - whichever is more restrictive applies
3. **Reservation**: If capacity available, reserves tokens immediately. Otherwise, queues the request with timeout.
4. **API Call**: Make your LLM API call
5. **Record Actual Usage** (Optional but recommended): Call `RecordActualUsage()` with actual tokens from response
   - If recorded: Actual usage tracked in sliding window for WindowSeconds
   - If not recorded: Reserved capacity freed immediately on disposal
6. **Disposal**: When `using` block ends, reservation is released and queued requests are processed
7. **Sliding Window Cleanup**:
   - Token timeline cleaned up every WindowSeconds
   - Request timeline cleaned up every RequestWindowSeconds (separate window for RPM)
   - Stale reservations removed after 10x WindowSeconds

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

### User-Facing Packages

- **TokenRateGate** ⭐: Complete solution - includes Base + OpenAI + Azure (recommended for most users)
- **TokenRateGate.Base**: Core engine + DI + Extensions (for custom LLM providers)
- **TokenRateGate.OpenAI**: OpenAI SDK integration (can be used with Base)
- **TokenRateGate.Azure**: Azure OpenAI SDK integration (can be used with Base)

### Internal Packages (Included in Base)

You don't need to install these individually - they're included in TokenRateGate.Base:
- TokenRateGate.Core: Core rate limiting engine
- TokenRateGate.Abstractions: Interfaces and abstractions
- TokenRateGate.Extensions: Base implementations
- TokenRateGate.Extensions.DependencyInjection: DI support

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Submit a pull request

## License

MIT License - Copyright © 2025 Marko Mrdja

See [LICENSE](LICENSE) for details.

## Acknowledgments

- Uses [tiktoken](https://github.com/openai/tiktoken) for accurate token counting
- Built for the [OpenAI SDK for .NET](https://github.com/openai/openai-dotnet)
- Supports [Azure OpenAI SDK](https://github.com/Azure/azure-sdk-for-net)
