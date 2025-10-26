using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Models;

namespace StreamingSample;

/// <summary>
/// This sample demonstrates handling streaming LLM responses with TokenRateGate.
/// Streaming responses arrive incrementally, making token estimation and actual usage tracking more complex.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        Console.WriteLine("=== TokenRateGate Streaming Sample ===\n");

        // Example 1: Basic streaming with upfront reservation
        await Example1_BasicStreaming(loggerFactory);

        // Example 2: Multiple concurrent streams
        await Example2_ConcurrentStreaming(loggerFactory);

        // Example 3: Handling stream cancellation
        await Example3_StreamCancellation(loggerFactory);

        // Example 4: Stream error handling
        await Example4_ErrorHandling(loggerFactory);

        Console.WriteLine("\n=== All examples completed ===");
    }

    /// <summary>
    /// Example 1: Basic streaming pattern with upfront token reservation
    /// </summary>
    static async Task Example1_BasicStreaming(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 1: Basic Streaming ---");
        Console.WriteLine("Simulating streaming LLM response with token tracking\n");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 5
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        // For streaming, we need to reserve tokens upfront based on estimation
        var inputTokens = 2000;
        var estimatedOutputTokens = 1500; // Conservative estimate

        Console.WriteLine($"Reserving {inputTokens} input + {estimatedOutputTokens} estimated output tokens");

        await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutputTokens);

        Console.WriteLine($"✓ Reservation granted: {reservation.ReservedTokens} tokens reserved\n");
        Console.WriteLine("Streaming response:");

        // Simulate streaming response
        var actualOutputTokens = 0;
        await foreach (var chunk in SimulateStreamingResponse(50))
        {
            Console.Write(chunk.Text);
            actualOutputTokens += chunk.TokenCount;
            await Task.Delay(50); // Simulate network delay between chunks
        }

        Console.WriteLine($"\n\nStream completed:");
        Console.WriteLine($"  Input tokens: {inputTokens}");
        Console.WriteLine($"  Actual output tokens: {actualOutputTokens}");
        Console.WriteLine($"  Total actual: {inputTokens + actualOutputTokens}");
        Console.WriteLine($"  Reserved: {reservation.ReservedTokens}");

        // Record actual usage after stream completes
        reservation.RecordActualUsage(inputTokens, actualOutputTokens);

        var efficiency = (double)(inputTokens + actualOutputTokens) / reservation.ReservedTokens;
        Console.WriteLine($"  Efficiency: {efficiency:P1}\n");
    }

    /// <summary>
    /// Example 2: Multiple concurrent streaming requests
    /// </summary>
    static async Task Example2_ConcurrentStreaming(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 2: Concurrent Streaming ---");
        Console.WriteLine("Processing multiple concurrent streams\n");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 150_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 3
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        // Start 3 concurrent streams
        var tasks = Enumerable.Range(1, 3).Select(async streamId =>
        {
            var inputTokens = Random.Shared.Next(1500, 2500);
            var estimatedOutput = Random.Shared.Next(1000, 2000);

            await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutput);

            Console.WriteLine($"Stream {streamId}: Started (reserved {reservation.ReservedTokens} tokens)");

            var actualOutput = 0;
            await foreach (var chunk in SimulateStreamingResponse(30))
            {
                actualOutput += chunk.TokenCount;
                await Task.Delay(Random.Shared.Next(30, 70));
            }

            reservation.RecordActualUsage(inputTokens, actualOutput);
            Console.WriteLine($"Stream {streamId}: Completed (actual: {inputTokens + actualOutput} tokens)");
        });

        await Task.WhenAll(tasks);
        Console.WriteLine("\nAll streams completed\n");
    }

    /// <summary>
    /// Example 3: Handling stream cancellation
    /// </summary>
    static async Task Example3_StreamCancellation(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 3: Stream Cancellation ---");
        Console.WriteLine("Demonstrating proper cleanup when streams are cancelled\n");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        using var cts = new CancellationTokenSource();

        try
        {
            var inputTokens = 2000;
            var estimatedOutput = 1500;

            await using var reservation = await rateGate.ReserveTokensAsync(
                inputTokens,
                estimatedOutput,
                cts.Token
            );

            Console.WriteLine($"Stream started, reserved {reservation.ReservedTokens} tokens");
            Console.WriteLine("Processing stream (will cancel after 5 chunks)...\n");

            var chunkCount = 0;
            var actualOutput = 0;

            await foreach (var chunk in SimulateStreamingResponse(20).WithCancellation(cts.Token))
            {
                Console.Write(chunk.Text);
                actualOutput += chunk.TokenCount;
                chunkCount++;

                if (chunkCount == 5)
                {
                    Console.WriteLine("\n\nCancelling stream...");
                    cts.Cancel();
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stream was cancelled");
            Console.WriteLine("\nImportant: The reservation is automatically released via IAsyncDisposable");
            Console.WriteLine("This ensures proper cleanup even when streams are cancelled\n");
        }
    }

    /// <summary>
    /// Example 4: Handling errors during streaming
    /// </summary>
    static async Task Example4_ErrorHandling(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 4: Error Handling ---");
        Console.WriteLine("Demonstrating error handling during streaming\n");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        try
        {
            var inputTokens = 2000;
            var estimatedOutput = 1500;

            await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, estimatedOutput);

            Console.WriteLine($"Stream started, reserved {reservation.ReservedTokens} tokens");
            Console.WriteLine("Processing stream (will simulate error)...\n");

            var actualOutput = 0;
            var chunkCount = 0;

            await foreach (var chunk in SimulateStreamingResponse(20))
            {
                Console.Write(chunk.Text);
                actualOutput += chunk.TokenCount;
                chunkCount++;

                // Simulate an error mid-stream
                if (chunkCount == 7)
                {
                    throw new InvalidOperationException("Simulated stream error (e.g., network issue)");
                }

                await Task.Delay(50);
            }

            // In case of successful completion
            reservation.RecordActualUsage(inputTokens, actualOutput);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"\n\n❌ Error: {ex.Message}");
            Console.WriteLine("\nImportant points:");
            Console.WriteLine("1. The reservation is automatically released via IAsyncDisposable");
            Console.WriteLine("2. If partial data was received, you could record partial usage");
            Console.WriteLine("3. The rate gate remains in a consistent state");
            Console.WriteLine("4. Other requests can continue without impact\n");
        }
    }

    /// <summary>
    /// Simulates a streaming LLM response by yielding chunks incrementally
    /// </summary>
    static async IAsyncEnumerable<StreamChunk> SimulateStreamingResponse(
        int totalChunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var words = new[] { "Hello", "world", "!", "This", "is", "a", "streaming", "response", "from",
                           "the", "LLM", ".", "Each", "chunk", "contains", "a", "few", "tokens", "." };

        for (int i = 0; i < totalChunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var word = words[i % words.Length];
            var tokenCount = Random.Shared.Next(1, 4); // Simulate variable token counts

            yield return new StreamChunk
            {
                Text = word + " ",
                TokenCount = tokenCount
            };

            await Task.Delay(10, cancellationToken); // Simulate streaming delay
        }
    }
}

/// <summary>
/// Represents a chunk in a streaming response
/// </summary>
class StreamChunk
{
    public string Text { get; set; } = string.Empty;
    public int TokenCount { get; set; }
}

