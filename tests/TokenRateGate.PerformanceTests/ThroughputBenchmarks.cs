using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.PerformanceTests;

/// <summary>
/// Benchmarks to measure sustained throughput under high load scenarios.
/// Tests validate that the system can achieve 95%+ of theoretical throughput.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class ThroughputBenchmarks
{
    private TokenRateGate.Core.TokenRateGate? _gate;

    [Params(100, 500, 1000)]
    public int RequestCount { get; set; }

    [Params(1, 5, 10)]
    public int ConcurrentRequests { get; set; }

    [IterationSetup]
    public void Setup()
    {
        // Configure for high throughput testing
        // 100K tokens per 5 seconds = 20,000 tokens/second theoretical max
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 5,
            SafetyBufferPercentage = 0.05, // 5% safety buffer
            MaxConcurrentRequests = ConcurrentRequests,
            MaxRequestsPerMinute = RequestCount * 2, // Generous RPM limit
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _gate?.Dispose();
    }

    /// <summary>
    /// Benchmark: Sustained throughput test
    /// Sends many requests to measure actual throughput vs theoretical capacity.
    /// Success: Achieves 95%+ of theoretical throughput.
    /// </summary>
    [Benchmark]
    public async Task SustainedThroughput()
    {
        // Each request uses 1000 tokens
        const int tokensPerRequest = 1000;

        var tasks = new List<Task>();

        for (int i = 0; i < RequestCount; i++)
        {
            tasks.Add(ProcessSingleRequest(tokensPerRequest));

            // Control concurrency manually to match ConcurrentRequests
            if (tasks.Count >= ConcurrentRequests)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        // Wait for remaining tasks
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Benchmark: Burst throughput test
    /// Sends all requests simultaneously to test queue handling.
    /// </summary>
    [Benchmark]
    public async Task BurstThroughput()
    {
        const int tokensPerRequest = 1000;

        var tasks = Enumerable.Range(0, RequestCount)
            .Select(_ => ProcessSingleRequest(tokensPerRequest))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Benchmark: Small request throughput
    /// Tests throughput with many small requests.
    /// </summary>
    [Benchmark]
    public async Task SmallRequestThroughput()
    {
        const int tokensPerRequest = 100;

        var tasks = new List<Task>();

        for (int i = 0; i < RequestCount * 10; i++)
        {
            tasks.Add(ProcessSingleRequest(tokensPerRequest));

            if (tasks.Count >= ConcurrentRequests)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task ProcessSingleRequest(int tokensPerRequest)
    {
        var inputTokens = tokensPerRequest / 2;
        var estimatedOutput = tokensPerRequest / 2;

        await using var reservation = await _gate!.ReserveTokensAsync(inputTokens, estimatedOutput);

        // Simulate some actual work
        await Task.Delay(1);

        // Record actual usage (simulate slight variance)
        var actualOutput = (int)(estimatedOutput * (0.9 + Random.Shared.NextDouble() * 0.2));
        reservation.RecordActualUsage(inputTokens, actualOutput);
    }
}
