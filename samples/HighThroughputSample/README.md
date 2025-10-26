# HighThroughputSample

Demonstrates high-throughput parallel processing with TokenRateGate, showing how to efficiently process large batches of LLM requests while respecting rate limits.

## What You'll Learn

- Batch processing with parallel execution
- Producer-Consumer pattern for continuous processing
- Handling backpressure when capacity is exceeded
- Performance metrics and throughput measurement
- Thread-safe statistics collection

## Examples

### Example 1: Batch Processing
Processes 100 requests in parallel using `Task.WhenAll`, demonstrating:
- Parallel execution with rate limiting
- Throughput measurement
- Token rate calculation

### Example 2: Producer-Consumer Pattern
Implements a work queue with multiple consumers:
- Using `System.Threading.Channels` for efficient queuing
- Multiple consumer tasks processing from shared queue
- Graceful shutdown handling

### Example 3: Backpressure Handling
Demonstrates system behavior under capacity constraints:
- Wait time tracking
- Timeout handling
- Graceful degradation strategies

## Key Patterns

### Parallel Processing
```csharp
var tasks = Enumerable.Range(1, 100).Select(async i =>
{
    await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, outputTokens);
    // Process request
});
await Task.WhenAll(tasks);
```

### Producer-Consumer
```csharp
var channel = Channel.CreateBounded<Request>(50);

// Producer
await channel.Writer.WriteAsync(request);

// Consumer
await foreach (var request in channel.Reader.ReadAllAsync())
{
    await using var reservation = await rateGate.ReserveTokensAsync(...);
    // Process
}
```

## Running

```bash
cd samples/HighThroughputSample
dotnet run
```

## Performance Considerations

- **MaxConcurrentRequests**: Controls parallelism level
- **Batching**: Group requests to maximize throughput
- **Channels**: Use bounded channels to prevent memory issues
- **Metrics**: Track throughput and wait times for optimization
