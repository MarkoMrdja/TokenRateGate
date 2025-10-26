# BasicUsage Sample

This sample demonstrates basic TokenRateGate usage without dependency injection. It shows manual integration with explicit resource management.

## What You'll Learn

- How to configure TokenRateGate with custom limits
- Making token reservations for LLM API calls
- Recording actual usage for accurate tracking
- Handling queue behavior when capacity is exhausted
- Monitoring usage statistics

## Examples Included

### Example 1: Simple Usage
Shows the basic flow of configuring TokenRateGate, reserving tokens, and recording actual usage.

### Example 2: Custom Configuration
Demonstrates advanced configuration options including safety buffers, concurrency limits, and request-per-minute limiting.

### Example 3: Queue Behavior
Shows how TokenRateGate handles requests when capacity is exhausted, demonstrating the queuing mechanism.

### Example 4: Actual Usage Tracking
Explains the importance of recording actual token usage vs estimated usage, and how it impacts efficiency.

### Example 5: Monitoring Statistics
Demonstrates how to retrieve and interpret usage statistics for monitoring and observability.

## Running the Sample

```bash
cd samples/BasicUsage
dotnet run
```

## Key Concepts

### Token Reservation
```csharp
await using var reservation = await rateGate.ReserveTokensAsync(estimatedTokens);
```
The `await using` pattern ensures the reservation is properly released when done.

### Recording Actual Usage
```csharp
reservation.RecordActualUsage(actualTokens);
```
Always record actual usage from the API response for accurate capacity tracking.

### Usage Statistics
```csharp
var stats = rateGate.GetUsageStats();
Console.WriteLine($"Usage: {stats.UsagePercentage:P1}");
```
Monitor capacity utilization in real-time.

## Configuration Options Demonstrated

- `TokenLimit`: Maximum tokens per time window
- `WindowSeconds`: Time window for token limiting
- `SafetyBuffer`: Reserved capacity to avoid hitting exact limits
- `MaxConcurrentRequests`: Limit on concurrent API calls
- `MaxRequestsPerMinute`: Additional RPM limiting
- `MaxWaitTime`: Maximum time to wait when queued
- `OutputEstimationStrategy`: How to estimate output tokens

## Next Steps

After understanding the basics, check out:
- **DependencyInjectionSample**: Integration with ASP.NET Core DI
- **MultiTenantSample**: Managing multiple rate limit pools
- **HighThroughputSample**: Parallel processing patterns
