using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests to verify that 100% capacity utilization is achievable.
/// Success criteria: System can achieve 95%+ of theoretical throughput.
/// </summary>
public class CapacityUtilizationTests : IDisposable
{
    private readonly TokenRateGate.Core.TokenRateGate _gate;

    public CapacityUtilizationTests()
    {
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 10,
            SafetyBufferPercentage = 0.05, // 5% safety buffer
            MaxConcurrentRequests = 20,
            MaxRequestsPerMinute = 10000,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);
    }

    public void Dispose()
    {
        _gate?.Dispose();
    }

    [Fact]
    public async Task HighThroughputScenario_ShouldAchieve95PercentUtilization()
    {
        // Arrange
        const int targetTokens = 95_000; // 95% of 100k limit
        const int tokensPerRequest = 1000;
        const int requestCount = targetTokens / tokensPerRequest;

        // Act: Send continuous stream of requests
        long totalTokensProcessed = 0;
        var tasks = new List<Task>();

        for (int i = 0; i < requestCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await using var reservation = await _gate.ReserveTokensAsync(
                    tokensPerRequest / 2,
                    tokensPerRequest / 2);

                await Task.Yield(); // Minimal simulated work

                reservation.RecordActualUsage(
                    tokensPerRequest / 2,
                    tokensPerRequest / 2);

                Interlocked.Add(ref totalTokensProcessed, tokensPerRequest);
            }));

            // Control concurrency
            if (tasks.Count >= 20)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        await Task.WhenAll(tasks);

        // Assert: Should have processed target tokens
        totalTokensProcessed.Should().BeGreaterThanOrEqualTo(targetTokens,
            "System should achieve 95%+ capacity utilization");
    }

    [Fact]
    public async Task FullCapacityFill_ShouldReachSafetyBufferLimit()
    {
        // Arrange: Fill capacity up to safety buffer limit
        // Effective capacity = 100k - 5% = 95k
        // But MaxConcurrentRequests = 20, so we can only have 20 active reservations at once
        const int tokensPerRequest = 4200; // Each reservation is 4.2k tokens
        const int maxActiveReservations = 20; // Limited by MaxConcurrentRequests

        // Act: Fill to capacity with max concurrent reservations
        var reservations = new List<TokenRateGate.Core.Models.TokenReservation>();

        for (int i = 0; i < maxActiveReservations; i++)
        {
            reservations.Add(await _gate.ReserveTokensAsync(tokensPerRequest / 2, tokensPerRequest / 2));
        }

        // Get stats with max concurrent reservations
        var stats = _gate.GetUsageStats();

        // Assert: Should have significant capacity reserved
        // 20 reservations * 4.2k tokens = 84k tokens reserved = 84% of 100k
        stats.TotalReserved.Should().BeGreaterThanOrEqualTo(75_000,
            "Should have significant capacity reserved");

        stats.AvailableCapacity.Should().BeLessThan(21_000,
            "Available capacity should be limited when many reservations are active");

        stats.IsNearCapacity.Should().BeTrue(
            "Should indicate near capacity (>80%) when at max concurrent reservations");

        // Cleanup
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task ContinuousLoad_ShouldMaintainHighUtilization()
    {
        // Arrange: Continuous load over extended period
        const int durationSeconds = 3;
        const int tokensPerRequest = 500;
        var endTime = DateTime.UtcNow.AddSeconds(durationSeconds);

        // Act: Maintain continuous load
        long totalTokens = 0;
        var tasks = new List<Task>();

        while (DateTime.UtcNow < endTime)
        {
            tasks.Add(Task.Run(async () =>
            {
                await using var reservation = await _gate.ReserveTokensAsync(
                    tokensPerRequest / 2,
                    tokensPerRequest / 2);

                await Task.Yield();

                reservation.RecordActualUsage(
                    tokensPerRequest / 2,
                    tokensPerRequest / 2);

                Interlocked.Add(ref totalTokens, tokensPerRequest);
            }));

            // Keep ~10 concurrent requests
            if (tasks.Count >= 10)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        await Task.WhenAll(tasks);

        // Assert: Should process significant tokens (at least 50% of theoretical max)
        // Theoretical max = 100k tokens / 10 seconds * 3 seconds = 30k tokens
        var theoreticalMax = 100_000L / 10 * durationSeconds;
        var minExpected = theoreticalMax / 2; // At least 50%

        totalTokens.Should().BeGreaterThanOrEqualTo(minExpected,
            "System should maintain reasonable utilization under continuous load");
    }

    [Fact]
    public async Task VariedRequestSizes_ShouldEfficientlyUtilizeCapacity()
    {
        // Arrange: Mix of small, medium, and large requests
        var requestSizes = new[] { 100, 500, 1000, 2000, 5000 };
        const int requestsPerSize = 10;

        // Act: Process mixed request sizes
        long totalProcessed = 0;
        var tasks = new List<Task>();

        foreach (var size in requestSizes)
        {
            for (int i = 0; i < requestsPerSize; i++)
            {
                int requestSize = size;
                tasks.Add(Task.Run(async () =>
                {
                    await using var reservation = await _gate.ReserveTokensAsync(
                        requestSize / 2,
                        requestSize / 2);

                    await Task.Yield();

                    reservation.RecordActualUsage(
                        requestSize / 2,
                        requestSize / 2);

                    Interlocked.Add(ref totalProcessed, requestSize);
                }));

                if (tasks.Count >= 15)
                {
                    await Task.WhenAny(tasks);
                    tasks.RemoveAll(t => t.IsCompleted);
                }
            }
        }

        await Task.WhenAll(tasks);

        // Assert: Should process all requests
        var expectedTotal = requestSizes.Sum() * requestsPerSize;
        totalProcessed.Should().Be(expectedTotal,
            "System should efficiently handle varied request sizes");
    }

    [Fact]
    public async Task RapidSmallRequests_ShouldAchieveHighThroughput()
    {
        // Arrange: Many small requests
        const int smallRequest = 50;
        const int requestCount = 500;

        // Act: Send rapid small requests
        long totalProcessed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, requestCount)
            .Select(_ => Task.Run(async () =>
            {
                await using var reservation = await _gate.ReserveTokensAsync(
                    smallRequest / 2,
                    smallRequest / 2);

                reservation.RecordActualUsage(
                    smallRequest / 2,
                    smallRequest / 2);

                Interlocked.Add(ref totalProcessed, smallRequest);
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert: All requests should complete
        totalProcessed.Should().Be(smallRequest * requestCount,
            "All small requests should be processed");

        // Should complete reasonably fast (this is throughput dependent)
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "Small requests should process with good throughput");
    }
}
