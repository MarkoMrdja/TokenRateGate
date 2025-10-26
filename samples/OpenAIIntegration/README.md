# OpenAI Integration Sample

This sample demonstrates how to use **TokenRateGate** with the **OpenAI .NET SDK** to enforce token-based rate limiting on LLM API requests.

## Features Demonstrated

1. **Basic Chat Completion** - Simple rate-limited OpenAI chat requests
2. **Streaming Responses** - Rate-limited streaming chat completions
3. **Parallel Requests** - Multiple concurrent requests with rate limiting
4. **Usage Monitoring** - Track token consumption and capacity utilization
5. **Configuration Strategies** - Different estimation strategies for various use cases

## Prerequisites

- **.NET 9.0 SDK** or later
- **OpenAI API Key** (optional for demonstration mode)

## Running the Sample

### Without API Key (Demonstration Mode)

Run the sample to see code examples and patterns without making actual API calls:

```bash
dotnet run --project samples/OpenAIIntegration/OpenAIIntegration.csproj
```

### With OpenAI API Key (Real API Calls)

Set your OpenAI API key and run:

```bash
# On Unix/Mac/Linux
export OPENAI_API_KEY="sk-..."
dotnet run --project samples/OpenAIIntegration/OpenAIIntegration.csproj

# On Windows (Command Prompt)
set OPENAI_API_KEY=sk-...
dotnet run --project samples/OpenAIIntegration/OpenAIIntegration.csproj

# On Windows (PowerShell)
$env:OPENAI_API_KEY="sk-..."
dotnet run --project samples/OpenAIIntegration/OpenAIIntegration.csproj
```

## Key Concepts

### Basic Setup

```csharp
// 1. Configure rate limits (match your OpenAI tier)
var options = new TokenRateGateOptions
{
    TokenLimit = 100000,        // 100K tokens per minute (adjust for your tier)
    WindowSeconds = 60,
    MaxConcurrentRequests = 5,
    OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
    OutputMultiplier = 0.5      // Estimate output as 50% of input
};

// 2. Create rate gate
var rateGate = new TokenRateGate(Options.Create(options), logger);

// 3. Create OpenAI client
var client = new ChatClient("gpt-4", apiKey);

// 4. Create helper for your model
var helper = new OpenAIChatHelper("gpt-4", options);

// 5. Make rate-limited API calls
var messages = new List<ChatMessage> { new UserChatMessage("Hello!") };
var response = await rateGate.ExecuteChatAsync(client, messages, helper);
```

### Streaming Responses

```csharp
await foreach (var update in rateGate.ExecuteChatStreamingAsync(
    client, messages, helper))
{
    if (update.ContentUpdate.Count > 0)
        Console.Write(update.ContentUpdate[0].Text);
}
```

### Monitoring Usage

```csharp
var stats = rateGate.GetUsageStats();
Console.WriteLine($"Tokens Used: {stats.TokensInWindow}/{stats.TokenLimit}");
Console.WriteLine($"Available: {stats.AvailableTokens}");
Console.WriteLine($"Waiting: {stats.WaitingRequests}");
```

## Configuration Strategies

### Conservative (Best for strict budgets)
```csharp
var options = new TokenRateGateOptions
{
    TokenLimit = 50000,
    OutputEstimationStrategy = OutputEstimationStrategy.Conservative,
    MaxConcurrentRequests = 3
};
```
- **Pros**: Won't exceed limits, predictable costs
- **Cons**: May under-utilize capacity

### Optimized (Best for throughput)
```csharp
var options = new TokenRateGateOptions
{
    TokenLimit = 100000,
    OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
    OutputMultiplier = 0.6,     // Tune based on actual usage
    MaxConcurrentRequests = 10
};
```
- **Pros**: Better throughput and capacity utilization
- **Cons**: Requires tuning the multiplier

### Fixed Output (Best for predictable responses)
```csharp
var options = new TokenRateGateOptions
{
    TokenLimit = 100000,
    OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount,
    DefaultOutputTokens = 1000,  // Each response ~1000 tokens
    MaxConcurrentRequests = 5
};
```
- **Pros**: Predictable reservation sizes
- **Cons**: May waste capacity if output varies

## OpenAI Rate Limits by Tier

Configure `TokenLimit` based on your OpenAI account tier:

| Tier | GPT-4 | GPT-4o | GPT-3.5-Turbo |
|------|-------|--------|---------------|
| Free | 10K TPM | 10K TPM | 40K TPM |
| Tier 1 | 10K TPM | 30K TPM | 60K TPM |
| Tier 2 | 50K TPM | 150K TPM | 250K TPM |
| Tier 3 | 100K TPM | 300K TPM | 1M TPM |
| Tier 4 | 300K TPM | 800K TPM | 2M TPM |

TPM = Tokens Per Minute

> **Note**: These are approximate values as of 2025. Check [OpenAI's rate limits page](https://platform.openai.com/docs/guides/rate-limits) for current values.

## Best Practices

1. **Start Conservative**: Begin with `OutputEstimationStrategy.Conservative` and tune based on actual usage
2. **Monitor Efficiency**: Check `stats.EfficiencyPercent` to see how accurate your estimates are
3. **Set Appropriate Concurrency**: Match `MaxConcurrentRequests` to your application's needs
4. **Use Streaming for Long Responses**: Provides better user experience and allows early cancellation
5. **Handle Rate Limit Exceptions**: The gate will queue requests when capacity is exhausted

## Troubleshooting

### Requests are queuing excessively
- Your `TokenLimit` might be too low for your workload
- Consider increasing `OutputMultiplier` if you're over-estimating
- Check if you have requests stuck waiting (possible bug)

### Token estimation is inaccurate
- Switch to `FixedMultiplier` and tune the multiplier
- Monitor actual vs estimated usage via logging
- Consider `FixedAmount` if response sizes are predictable

### Getting 429 errors from OpenAI
- Your configured `TokenLimit` exceeds your OpenAI tier's actual limit
- Reduce `TokenLimit` to match your tier
- Add safety buffer by reducing limit by 10-20%

## Related Documentation

- [TokenRateGate Core Documentation](../../src/TokenRateGate.Core/README.md)
- [OpenAI SDK Documentation](https://github.com/openai/openai-dotnet)
- [OpenAI Rate Limits Guide](https://platform.openai.com/docs/guides/rate-limits)

## Cost Optimization Tips

1. **Use GPT-4o-mini for testing**: 60x cheaper than GPT-4
2. **Set aggressive timeout**: Use `MaxWaitTime` to fail fast on capacity issues
3. **Monitor reservation efficiency**: Over-reservation wastes capacity
4. **Batch similar requests**: Reduces overhead and improves throughput

## License

This sample is part of the TokenRateGate project and is licensed under the MIT License.
