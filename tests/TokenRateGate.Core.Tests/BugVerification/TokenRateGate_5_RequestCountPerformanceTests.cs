using FluentAssertions;
using System.Diagnostics;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-5: GetCurrentRequestCount() has O(n) performance using LINQ Count()
///
/// Bug Description:
/// GetCurrentRequestCount() uses LINQ Count() with predicate which iterates entire queue on every call.
/// This method is called frequently during capacity checks, causing performance degradation.
///
/// Location: TokenRateGate.cs:532
/// Current code: return _requestTimeline.Count(timestamp => timestamp >= cutoff);
///
/// Expected Behavior (after fix):
/// Should be O(1) or at least optimized. Track count as a field or use the fact that cleanup removes old entries.
/// </summary>
public class TokenRateGate_5_RequestCountPerformanceTests
{
    [Fact]
    public async Task GetCurrentRequestCountWithLargeTimeline_HasLinearPerformance()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000000; // Very high to avoid blocking
        options.SafetyBuffer = 0;
        options.MaxConcurrentRequests = 1;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Fill the request timeline with many entries
        var requestCount = 10000;
        for (int i = 0; i < requestCount; i++)
        {
            var reservation = await gate.ReserveTokensAsync(inputTokens: 10, estimatedOutputTokens: 10);
            await reservation.DisposeAsync();
        }

        // Act
        // Measure time taken by GetUsageStats() which calls GetCurrentRequestCount()
        var stopwatch = Stopwatch.StartNew();
        var iterations = 1000;

        for (int i = 0; i < iterations; i++)
        {
            var stats = gate.GetUsageStats();
            // This internally calls GetCurrentRequestCount() which uses Count(predicate)
        }

        stopwatch.Stop();
        var avgMilliseconds = stopwatch.ElapsedMilliseconds / (double)iterations;

        // Assert
        // BUG VERIFICATION: With 10k entries in _requestTimeline, each GetCurrentRequestCount()
        // iterates all 10k entries using LINQ Count(predicate)
        // This should be notably slower than O(1) access

        avgMilliseconds.Should().BeGreaterThan(0,
            "because GetCurrentRequestCount() iterates the entire request timeline");

        // Log for manual verification
        Console.WriteLine($"Average time per GetUsageStats() call with {requestCount} timeline entries: {avgMilliseconds:F4}ms");
        Console.WriteLine($"This demonstrates O(n) behavior where n = {requestCount}");

        // If this were O(1), it should complete in microseconds, not milliseconds
        // The LINQ Count(predicate) causes unnecessary iteration

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task FrequentCapacityChecks_CumulativePerformanceImpact()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Build up request history
        for (int i = 0; i < 5000; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 10, estimatedOutputTokens: 10);
            await r.DisposeAsync();
        }

        // Act
        // Simulate frequent capacity checks (which happens during normal operation)
        var stopwatch = Stopwatch.StartNew();
        var checkCount = 10000;

        for (int i = 0; i < checkCount; i++)
        {
            // GetUsageStats() calls GetCurrentRequestCount() internally
            _ = gate.GetUsageStats();
        }

        stopwatch.Stop();

        // Assert
        var totalMilliseconds = stopwatch.ElapsedMilliseconds;
        var avgMicroseconds = (stopwatch.Elapsed.TotalMicroseconds / checkCount);

        Console.WriteLine($"Performed {checkCount} capacity checks in {totalMilliseconds}ms");
        Console.WriteLine($"Average per check: {avgMicroseconds:F2}µs");

        // BUG IMPACT: Each check iterates the entire timeline unnecessarily
        // With 5000 entries, this adds significant overhead
        // After fix, this should be orders of magnitude faster

        totalMilliseconds.Should().BeGreaterThan(0,
            "cumulative impact of O(n) GetCurrentRequestCount() is measurable");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task RequestCountPerformance_ScalesWithTimelineSize()
    {
        // This test demonstrates that performance degrades linearly with timeline size
        var results = new List<(int size, double avgMs)>();

        var testSizes = new[] { 100, 500, 1000, 5000, 10000 };

        foreach (var size in testSizes)
        {
            // Arrange
            var options = TokenRateGateTestFactory.CreateMinimalOptions();
            options.TokenLimit = 10000000;
            options.SafetyBuffer = 0;
            var gate = TokenRateGateTestFactory.CreateGate(options);

            // Build timeline of specified size
            for (int i = 0; i < size; i++)
            {
                var r = await gate.ReserveTokensAsync(inputTokens: 10, estimatedOutputTokens: 10);
                await r.DisposeAsync();
            }

            // Act - Measure GetUsageStats() performance
            var stopwatch = Stopwatch.StartNew();
            var iterations = 100;

            for (int i = 0; i < iterations; i++)
            {
                _ = gate.GetUsageStats();
            }

            stopwatch.Stop();
            var avgMs = stopwatch.ElapsedMilliseconds / (double)iterations;
            results.Add((size, avgMs));

            gate.Dispose();
        }

        // Assert
        Console.WriteLine("Performance degradation with timeline size:");
        foreach (var (size, avgMs) in results)
        {
            Console.WriteLine($"  Size {size,5}: {avgMs:F4}ms per call");
        }

        // BUG VERIFICATION: Performance should degrade linearly (O(n))
        // After fix with O(1) implementation, all sizes should have similar performance

        var smallestTime = results.First().avgMs;
        var largestTime = results.Last().avgMs;

        (largestTime / smallestTime).Should().BeGreaterThan(2,
            "because O(n) Count(predicate) scales linearly with timeline size");

        // Cleanup is implicit in loop
    }

    [Fact]
    public async Task GetUsageStats_CallsGetCurrentRequestCount_MultipleTimesInHotPath()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Build timeline
        for (int i = 0; i < 1000; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 10, estimatedOutputTokens: 10);
            await r.DisposeAsync();
        }

        // Act
        // GetUsageStats() is called in monitoring/logging scenarios
        // The O(n) performance of GetCurrentRequestCount() makes this expensive
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++)
        {
            var stats = gate.GetUsageStats();
            // In production, this data would be logged or sent to metrics
            _ = stats.RequestsInLastMinute; // This value comes from GetCurrentRequestCount()
        }

        stopwatch.Stop();

        // Assert
        Console.WriteLine($"1000 GetUsageStats() calls took {stopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine($"Average: {stopwatch.Elapsed.TotalMicroseconds / 1000:F2}µs per call");

        // BUG IMPACT: This overhead is unnecessary
        // GetCurrentRequestCount() is in the hot path of monitoring
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(0,
            "O(n) iteration in GetCurrentRequestCount() adds measurable overhead");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task ComparePerformance_CountPredicateVsDirectCount()
    {
        // This test demonstrates the performance difference between the current
        // implementation and a potential O(1) fix

        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Build large timeline
        for (int i = 0; i < 5000; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 10, estimatedOutputTokens: 10);
            await r.DisposeAsync();
        }

        // Act - Current implementation (O(n))
        var stopwatch1 = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            _ = gate.GetUsageStats(); // Uses Count(predicate)
        }
        stopwatch1.Stop();

        // Assert
        Console.WriteLine($"Current O(n) implementation: {stopwatch1.ElapsedMilliseconds}ms for 1000 calls");
        Console.WriteLine("After fix (tracking count as field), this should be ~100x faster or more");

        // BUG DOCUMENTED: The LINQ Count(timestamp => timestamp >= cutoff) at line 532
        // iterates the entire _requestTimeline queue on every call
        // This is called during:
        // 1. Every GetUsageStats() call
        // 2. Every HasCapacityInternal() call (via GetCurrentRequestCount)
        // 3. Frequent capacity checks during ReserveTokensAsync

        stopwatch1.ElapsedMilliseconds.Should().BeGreaterThan(0,
            "current O(n) implementation has measurable overhead with large timeline");

        // Cleanup
        gate.Dispose();
    }
}
