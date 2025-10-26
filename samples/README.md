# TokenRateGate Samples

This directory contains sample applications demonstrating various usage patterns of TokenRateGate.

## Available Samples

### 1. BasicUsage ✅
**Status**: Complete
**Description**: Demonstrates basic TokenRateGate usage without dependency injection.

**What you'll learn**:
- Manual TokenRateGate configuration and setup
- Reserving tokens for LLM requests
- Recording actual token usage
- Queue behavior when capacity is exhausted
- Monitoring usage statistics

**Run it**:
```bash
cd BasicUsage
dotnet run
```

### 2. DependencyInjectionSample 🚧
**Status**: Planned
**Description**: Integration with ASP.NET Core dependency injection.

**What you'll learn**:
- Registering TokenRateGate with `IServiceCollection`
- Using `ITokenRateGateFactory` for multi-tenant scenarios
- Configuration binding from appsettings.json
- Health checks integration

### 3. MultiTenantSample 🚧
**Status**: Planned
**Description**: Managing multiple rate limit pools for different tenants or API keys.

**What you'll learn**:
- Creating named TokenRateGate instances
- Per-tenant rate limiting
- Dynamic rate limit configuration
- Tenant isolation

### 4. HighThroughputSample 🚧
**Status**: Planned
**Description**: Parallel processing with rate limit throttling.

**What you'll learn**:
- Processing large batches of requests
- Concurrent request handling
- Performance optimization
- Backpressure handling

### 5. StreamingSample 🚧
**Status**: Planned
**Description**: Handling streaming LLM responses.

**What you'll learn**:
- Rate limiting for streaming APIs
- Token estimation for streams
- Recording actual usage mid-stream
- Error handling for partial responses

## Getting Started

Each sample is a self-contained console application. Navigate to the sample directory and run:

```bash
dotnet run
```

## Common Concepts

### Token Reservation Pattern

All samples follow this pattern:

```csharp
// 1. Reserve tokens before making LLM request
await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutputTokens);

// 2. Make your LLM API call
var response = await llmClient.SendRequestAsync(request);

// 3. Record actual usage from response
reservation.RecordActualUsage(response.TotalTokens);
```

### Configuration Options

Key configuration options used across samples:

```csharp
var options = new TokenRateGateOptions
{
    TokenLimit = 1_000_000,          // Tokens per window
    WindowSeconds = 60,               // Time window in seconds
    SafetyBuffer = 50_000,            // Reserved capacity
    MaxConcurrentRequests = 10,       // Concurrent request limit
    MaxRequestsPerMinute = 500,       // RPM limit
    MaxWaitTime = TimeSpan.FromMinutes(2)  // Queue timeout
};
```

## Prerequisites

- .NET 9.0 SDK or later
- Basic understanding of async/await patterns
- Familiarity with your LLM API provider's rate limits

## Next Steps

After exploring the samples:

1. **Read the Documentation**: Check out the full API documentation
2. **Integrate with Your LLM Provider**: See OpenAI and Azure integration packages
3. **Monitor in Production**: Set up OpenTelemetry metrics
4. **Tune Configuration**: Adjust settings based on your workload

## Contributing

Have an idea for a sample? Open an issue or submit a PR!

## Support

- **Issues**: [GitHub Issues](https://github.com/your-repo/issues)
- **Discussions**: [GitHub Discussions](https://github.com/your-repo/discussions)
- **Documentation**: [Full Docs](https://your-docs-site.com)
