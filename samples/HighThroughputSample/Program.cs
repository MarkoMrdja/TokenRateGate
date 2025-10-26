using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Models;

namespace HighThroughputSample;

/// <summary>
/// This sample demonstrates high-throughput parallel processing with TokenRateGate.
/// It shows how to efficiently process large batches of LLM requests while respecting rate limits.
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

        var logger = loggerFactory.CreateLogger<Program>();

        Console.WriteLine("=== TokenRateGate High Throughput Sample ===\n");

        // Example 1: Batch processing with parallelism
        await Example1_BatchProcessing(loggerFactory);

        // Example 2: Producer-Consumer pattern
        await Example2_ProducerConsumer(loggerFactory);

        // Example 3: Handling backpressure
        await Example3_Backpressure(loggerFactory);

        // Example 4: TRUE GATE BEHAVIOR - All requests eventually succeed
        await TrueGateBehaviorExample.RunAsync(loggerFactory);

        Console.WriteLine("\n=== All examples completed ===");
    }

    /// <summary>
    /// Example 1: Processing a large batch of requests in parallel
    /// </summary>
    static async Task Example1_BatchProcessing(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 1: Batch Processing ---");
        Console.WriteLine("Processing 100 requests with parallel execution\n");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 200_000,            // 200K tokens per minute
            WindowSeconds = 60,
            MaxConcurrentRequests = 10,       // Allow 10 concurrent requests
            SafetyBufferPercentage = 0.10     // 10% safety buffer (20K tokens)
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        const int totalRequests = 100;
        var stopwatch = Stopwatch.StartNew();
        var successCount = 0;
        var failureCount = 0;
        var totalTokens = 0;

        // Process requests in parallel using Task.WhenAll
        var tasks = Enumerable.Range(1, totalRequests).Select(async i =>
        {
            try
            {
                var inputTokens = Random.Shared.Next(1000, 2000);
                var outputTokens = Random.Shared.Next(500, 1000);

                await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, outputTokens);

                // Simulate LLM API call
                await Task.Delay(Random.Shared.Next(50, 150));

                var actualTokens = (int)(reservation.ReservedTokens * (0.9 + Random.Shared.NextDouble() * 0.2));
                var actualInput = inputTokens;
                var actualOutput = actualTokens - actualInput;
                reservation.RecordActualUsage(actualInput, actualOutput);

                Interlocked.Increment(ref successCount);
                Interlocked.Add(ref totalTokens, actualTokens);

                if (i % 10 == 0)
                {
                    Console.WriteLine($"  Completed {i}/{totalRequests} requests");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failureCount);
                Console.WriteLine($"  Request {i} failed: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Console.WriteLine($"\nResults:");
        Console.WriteLine($"  Total time: {stopwatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Successful: {successCount}");
        Console.WriteLine($"  Failed: {failureCount}");
        Console.WriteLine($"  Total tokens: {totalTokens:N0}");
        Console.WriteLine($"  Throughput: {totalRequests / stopwatch.Elapsed.TotalSeconds:F1} requests/second");
        Console.WriteLine($"  Token rate: {totalTokens / stopwatch.Elapsed.TotalSeconds:F0} tokens/second\n");
    }

    /// <summary>
    /// Example 2: Producer-Consumer pattern for continuous processing
    /// </summary>
    static async Task Example2_ProducerConsumer(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 2: Producer-Consumer Pattern ---");
        Console.WriteLine("Continuous request processing with work queue\n");

        var options = new TokenRateGateOptions
        {
            TokenLimit = 150_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 8
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        var requestQueue = System.Threading.Channels.Channel.CreateBounded<LlmRequest>(
            new System.Threading.Channels.BoundedChannelOptions(50)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
            }
        );

        var cts = new CancellationTokenSource();
        var processedCount = 0;

        // Producer: Generate requests
        var producer = Task.Run(async () =>
        {
            for (int i = 1; i <= 50; i++)
            {
                var request = new LlmRequest
                {
                    Id = i,
                    InputTokens = Random.Shared.Next(1500, 2500),
                    EstimatedOutputTokens = Random.Shared.Next(800, 1200)
                };

                await requestQueue.Writer.WriteAsync(request, cts.Token);
                await Task.Delay(50); // Simulate requests arriving over time
            }

            requestQueue.Writer.Complete();
            Console.WriteLine("Producer: Finished generating requests");
        });

        // Consumer: Process requests with rate limiting
        var consumers = Enumerable.Range(0, 4).Select(async consumerId =>
        {
            await foreach (var request in requestQueue.Reader.ReadAllAsync(cts.Token))
            {
                try
                {
                    await using var reservation = await rateGate.ReserveTokensAsync(
                        request.InputTokens,
                        request.EstimatedOutputTokens,
                        cts.Token
                    );

                    // Simulate processing
                    await Task.Delay(Random.Shared.Next(100, 200), cts.Token);

                    var actualTotal = (int)(reservation.ReservedTokens * 0.95);
                    var actualInput = request.InputTokens;
                    var actualOutput = actualTotal - actualInput;
                    reservation.RecordActualUsage(actualInput, actualOutput);

                    var count = Interlocked.Increment(ref processedCount);
                    if (count % 10 == 0)
                    {
                        Console.WriteLine($"  Consumer {consumerId}: Processed {count} requests");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Consumer {consumerId}: Error processing request {request.Id}: {ex.Message}");
                }
            }
        }).ToArray();

        await Task.WhenAll(producer);
        await Task.WhenAll(consumers);

        Console.WriteLine($"\nCompleted: {processedCount} requests processed\n");
    }

    /// <summary>
    /// Example 3: Handling backpressure when rate limits are exceeded
    /// </summary>
    static async Task Example3_Backpressure(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("--- Example 3: Backpressure Handling ---");
        Console.WriteLine("Demonstrating system behavior under high load\n");

        // Configure with aggressive limits to trigger backpressure
        // Set MaxWaitTime = null to enable "true gate" behavior - all requests eventually succeed
        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,        // Low limit to force queuing
            WindowSeconds = 30,
            MaxConcurrentRequests = 3,
            MaxWaitTime = null  // Infinite wait - true "gate" behavior, no timeouts
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        var stopwatch = Stopwatch.StartNew();
        var stats = new ConcurrentStats();

        // Launch a burst of requests
        var tasks = Enumerable.Range(1, 30).Select(async i =>
        {
            var startTime = stopwatch.Elapsed;
            try
            {
                var inputTokens = Random.Shared.Next(2000, 3000);
                var outputTokens = Random.Shared.Next(1000, 1500);

                await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, outputTokens);

                var waitTime = stopwatch.Elapsed - startTime;
                stats.RecordWaitTime(waitTime);

                // Simulate work
                await Task.Delay(Random.Shared.Next(500, 1000));

                var actualTotal = (int)(reservation.ReservedTokens * 0.95);
                var actualInput = inputTokens;
                var actualOutput = actualTotal - actualInput;
                reservation.RecordActualUsage(actualInput, actualOutput);

                stats.IncrementSuccess();

                if (waitTime.TotalMilliseconds > 100)
                {
                    Console.WriteLine($"  Request {i}: Waited {waitTime.TotalMilliseconds:F0}ms for capacity");
                }
            }
            catch (OperationCanceledException)
            {
                stats.IncrementTimeout();
                Console.WriteLine($"  Request {i}: Timed out waiting for capacity");
            }
            catch (Exception ex)
            {
                stats.IncrementFailure();
                Console.WriteLine($"  Request {i}: Failed - {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Console.WriteLine($"\nBackpressure Results:");
        Console.WriteLine($"  Successful: {stats.SuccessCount}");
        Console.WriteLine($"  Timeouts: {stats.TimeoutCount}");
        Console.WriteLine($"  Failures: {stats.FailureCount}");
        Console.WriteLine($"  Average wait time: {stats.AverageWaitTime.TotalMilliseconds:F0}ms");
        Console.WriteLine($"  Max wait time: {stats.MaxWaitTime.TotalMilliseconds:F0}ms");
        Console.WriteLine($"\n  This demonstrates graceful degradation under capacity constraints.");
        Console.WriteLine($"  In production, you might implement retry logic or request shedding.\n");
    }
}

/// <summary>
/// Represents an LLM request in the processing queue
/// </summary>
class LlmRequest
{
    public int Id { get; set; }
    public int InputTokens { get; set; }
    public int EstimatedOutputTokens { get; set; }
}

/// <summary>
/// Thread-safe statistics collector
/// </summary>
class ConcurrentStats
{
    private int _successCount;
    private int _timeoutCount;
    private int _failureCount;
    private readonly List<TimeSpan> _waitTimes = new();
    private readonly object _lock = new();

    public int SuccessCount => _successCount;
    public int TimeoutCount => _timeoutCount;
    public int FailureCount => _failureCount;

    public void IncrementSuccess() => Interlocked.Increment(ref _successCount);
    public void IncrementTimeout() => Interlocked.Increment(ref _timeoutCount);
    public void IncrementFailure() => Interlocked.Increment(ref _failureCount);

    public void RecordWaitTime(TimeSpan waitTime)
    {
        lock (_lock)
        {
            _waitTimes.Add(waitTime);
        }
    }

    public TimeSpan AverageWaitTime
    {
        get
        {
            lock (_lock)
            {
                return _waitTimes.Count > 0
                    ? TimeSpan.FromMilliseconds(_waitTimes.Average(t => t.TotalMilliseconds))
                    : TimeSpan.Zero;
            }
        }
    }

    public TimeSpan MaxWaitTime
    {
        get
        {
            lock (_lock)
            {
                return _waitTimes.Count > 0
                    ? _waitTimes.Max()
                    : TimeSpan.Zero;
            }
        }
    }
}
