# StreamingSample

Demonstrates handling streaming LLM responses with TokenRateGate. Streaming responses arrive incrementally, requiring special handling for token estimation and usage tracking.

## What You'll Learn

- Upfront token reservation for streaming responses
- Recording actual usage after stream completion
- Handling concurrent streams
- Proper cleanup with cancellation
- Error handling during streaming

## Examples

### Example 1: Basic Streaming
Shows the fundamental pattern for streaming:
- Reserve tokens upfront based on estimation
- Process chunks as they arrive
- Record actual usage when stream completes
- Calculate reservation efficiency

### Example 2: Concurrent Streaming
Demonstrates multiple simultaneous streams:
- Managing multiple concurrent reservations
- Independent stream lifecycle
- Parallel stream processing

### Example 3: Stream Cancellation
Shows proper cleanup when streams are cancelled:
- User-initiated cancellation
- Automatic reservation release via `IAsyncDisposable`
- Cleanup guarantees

### Example 4: Error Handling
Demonstrates error handling during streaming:
- Mid-stream error scenarios
- Automatic reservation cleanup
- System consistency after errors
- Optional partial usage recording

## Key Patterns

### Stream Reservation Pattern
```csharp
// Reserve tokens upfront
await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutput);

// Process stream
var actualOutput = 0;
await foreach (var chunk in streamResponse)
{
    actualOutput += chunk.TokenCount;
}

// Record actual usage
reservation.RecordActualUsage(inputTokens + actualOutput);
```

### Cancellation Handling
```csharp
using var cts = new CancellationTokenSource();
await using var reservation = await rateGate.ReserveTokensAsync(tokens, cts.Token);

await foreach (var chunk in stream.WithCancellation(cts.Token))
{
    // Process chunk
    if (shouldCancel) cts.Cancel();
}
// Reservation automatically released even if cancelled
```

## Running

```bash
cd samples/StreamingSample
dotnet run
```

## Best Practices

1. **Conservative Estimation**: For streaming, estimate generously since you can't adjust mid-stream
2. **Record Actual Usage**: Always record actual usage after completion for accurate tracking
3. **Use IAsyncDisposable**: Rely on `await using` for automatic cleanup
4. **Handle Cancellation**: Properly support cancellation tokens
5. **Track Partial Results**: Consider recording partial usage if stream fails mid-way

## Real-World Usage

In production with actual LLM APIs:
```csharp
await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutput);

var actualOutput = 0;
await foreach (var chunk in llmClient.StreamCompletionAsync(prompt))
{
    Console.Write(chunk.Text);
    actualOutput += chunk.Tokens;
}

reservation.RecordActualUsage(inputTokens + actualOutput);
```
