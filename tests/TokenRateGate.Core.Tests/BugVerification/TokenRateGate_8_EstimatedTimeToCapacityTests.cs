using FluentAssertions;
using TokenRateGate.Core.Tests.TestHelpers;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-8: EstimatedTimeToCapacity calculation is completely wrong
///
/// Bug Description:
/// TokenUsageStats.EstimatedTimeToCapacity calculates requests per second instead of tokens per second.
/// The variable name is misleading and the calculation is fundamentally incorrect.
///
/// Location: TokenRateGate.Core/Models/TokenUsageStats.cs:46
/// Current code: var tokensPerSecond = (double)RequestsInLastMinute / 60.0;
///
/// Expected Behavior (after fix):
/// Should track actual token consumption rate over time, not request rate.
/// </summary>
public class TokenRateGate_8_EstimatedTimeToCapacityTests
{
    [Fact]
    public async Task EstimatedTimeToCapacity_UsesRequestCountNotTokens()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 100000;
        options.SafetyBuffer = 10000;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Make requests with LARGE token counts
        for (int i = 0; i < 10; i++)
        {
            var reservation = await gate.ReserveTokensAsync(inputTokens: 5000, estimatedOutputTokens: 5000);
            reservation.RecordActualUsage(10000); // Each request uses 10,000 tokens
            await reservation.DisposeAsync();
        }

        var stats = gate.GetUsageStats();

        // Assert
        // BUG VERIFICATION: Line 46 calculates:
        // var tokensPerSecond = (double)RequestsInLastMinute / 60.0;
        // This divides REQUEST COUNT (10) by 60, giving ~0.167 "tokens" per second
        // But we actually consumed 100,000 tokens total!

        Console.WriteLine($"Requests in last minute: {stats.RequestsInLastMinute}");
        Console.WriteLine($"Current usage: {stats.CurrentUsage}");
        Console.WriteLine($"Available tokens: {stats.AvailableTokens}");
        Console.WriteLine($"Estimated time to capacity: {stats.EstimatedTimeToCapacity}");

        // The calculation is: tokensPerSecond = RequestsInLastMinute / 60.0
        // = 10 / 60.0 = 0.167
        // Then: secondsToCapacity = AvailableTokens / tokensPerSecond
        // = AvailableCapacity / 0.167 = very large number

        if (stats.EstimatedTimeToCapacity.HasValue)
        {
            var estimated = stats.EstimatedTimeToCapacity.Value.TotalSeconds;

            // BUG: The estimate is completely wrong because it divides by request count, not token rate
            // With 10 requests consuming 100k tokens, the actual token rate is ~100k/60 = 1667 tokens/sec
            // But the buggy code calculates 10/60 = 0.167 "tokens"/sec

            // The estimated time is off by a factor of ~10,000!
            Console.WriteLine($"BUG: Estimated seconds to capacity: {estimated:F2}");
            Console.WriteLine($"This is calculated as: AvailableTokens / (RequestCount / 60)");
            Console.WriteLine($"Should be calculated as: AvailableTokens / (ActualTokensConsumed / WindowDuration)");

            estimated.Should().BeGreaterThan(0,
                "estimate was calculated but uses wrong formula (request count instead of token consumption)");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EstimatedTimeToCapacity_WrongFormulaLeadsToWrongPrediction()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 50000;
        options.SafetyBuffer = 5000; // Available: 45000
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act - Scenario 1: Many small requests
        for (int i = 0; i < 100; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 50, estimatedOutputTokens: 50);
            r.RecordActualUsage(100); // Small token usage
            await r.DisposeAsync();
        }

        var statsSmallRequests = gate.GetUsageStats();

        // Clear and test Scenario 2: Few large requests with same total tokens
        await Task.Delay(options.WindowSeconds * 1000 + 100); // Wait for window to expire

        for (int i = 0; i < 10; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 500, estimatedOutputTokens: 500);
            r.RecordActualUsage(1000); // Large token usage
            await r.DisposeAsync();
        }

        var statsLargeRequests = gate.GetUsageStats();

        // Assert
        // Both scenarios consumed the same total tokens (100 * 100 = 10,000 vs 10 * 1000 = 10,000)
        // So EstimatedTimeToCapacity should be similar

        // But BUG: The calculation uses request count, not token consumption
        // Scenario 1: RequestsInLastMinute = 100, tokensPerSecond = 100/60 = 1.67
        // Scenario 2: RequestsInLastMinute = 10, tokensPerSecond = 10/60 = 0.167

        Console.WriteLine("\nScenario 1 (100 small requests):");
        Console.WriteLine($"  Requests: {statsSmallRequests.RequestsInLastMinute}");
        Console.WriteLine($"  Estimated time: {statsSmallRequests.EstimatedTimeToCapacity}");

        Console.WriteLine("\nScenario 2 (10 large requests):");
        Console.WriteLine($"  Requests: {statsLargeRequests.RequestsInLastMinute}");
        Console.WriteLine($"  Estimated time: {statsLargeRequests.EstimatedTimeToCapacity}");

        // BUG VERIFICATION: Estimates differ by 10x despite same token consumption rate
        if (statsSmallRequests.EstimatedTimeToCapacity.HasValue &&
            statsLargeRequests.EstimatedTimeToCapacity.HasValue)
        {
            var ratio = statsLargeRequests.EstimatedTimeToCapacity.Value.TotalSeconds /
                        statsSmallRequests.EstimatedTimeToCapacity.Value.TotalSeconds;

            Console.WriteLine($"\nEstimate ratio: {ratio:F2}x");
            Console.WriteLine("BUG: Should be ~1.0x (same token rate), but is ~10x (using request count)");

            ratio.Should().BeGreaterThan(5,
                "because the buggy formula makes estimates differ wildly for same token consumption rate");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EstimatedTimeToCapacity_VariableNameIsMisleading()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 100000;
        options.SafetyBuffer = 10000;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        for (int i = 0; i < 20; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 1000, estimatedOutputTokens: 1000);
            r.RecordActualUsage(2000);
            await r.DisposeAsync();
        }

        var stats = gate.GetUsageStats();

        // Assert
        // BUG DOCUMENTATION: Line 46 in TokenUsageStats.cs:
        // var tokensPerSecond = (double)RequestsInLastMinute / 60.0;
        //
        // The variable is named "tokensPerSecond" but actually contains "requestsPerSecond"
        // This is misleading and caused the incorrect implementation

        Console.WriteLine($"RequestsInLastMinute: {stats.RequestsInLastMinute}");

        // What the code calculates (buggy):
        var buggyTokensPerSecond = (double)stats.RequestsInLastMinute / 60.0;
        Console.WriteLine($"Buggy 'tokensPerSecond': {buggyTokensPerSecond}");
        Console.WriteLine($"  ^ This is actually requestsPerSecond, not tokensPerSecond!");

        // What it SHOULD calculate:
        // Need to track actual token consumption over time
        // actualTokensPerSecond = totalTokensConsumed / windowDuration
        var actualTokenRate = (20 * 2000) / 60.0; // 20 requests * 2000 tokens / 60 seconds
        Console.WriteLine($"Actual tokens per second: {actualTokenRate}");

        buggyTokensPerSecond.Should().NotBe(actualTokenRate,
            "because the buggy formula uses request count, not token consumption");

        // The difference is massive: 0.33 vs 666.67 tokens/second
        var errorFactor = actualTokenRate / buggyTokensPerSecond;
        Console.WriteLine($"Error factor: {errorFactor:F2}x");

        errorFactor.Should().BeGreaterThan(100,
            "the buggy calculation is off by orders of magnitude");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EstimatedTimeToCapacity_MakesMonitoringDataUseless()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 100000;
        options.SafetyBuffer = 10000;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act - Simulate real usage pattern: high token requests
        for (int i = 0; i < 5; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 8000, estimatedOutputTokens: 8000);
            r.RecordActualUsage(16000); // Heavy token usage
            await r.DisposeAsync();
        }

        await Task.Delay(100); // Brief pause

        var stats = gate.GetUsageStats();

        // Assert
        // Monitoring dashboards would show EstimatedTimeToCapacity
        // But the value is completely wrong and misleading

        Console.WriteLine("=== Monitoring Dashboard (Buggy) ===");
        Console.WriteLine($"Current Usage: {stats.CurrentUsage:N0} tokens");
        Console.WriteLine($"Available: {stats.AvailableTokens:N0} tokens");
        Console.WriteLine($"Usage: {stats.UsagePercentage:F1}%");
        Console.WriteLine($"Estimated time to capacity: {stats.EstimatedTimeToCapacity}");

        // BUG IMPACT:
        // - Actual token rate: (5 * 16000) / 60 = 1,333 tokens/sec
        // - Buggy calculation: 5 / 60 = 0.083 "tokens"/sec
        // - If available capacity is 10,000 tokens:
        //   - Real time to capacity: 10000 / 1333 = ~7.5 seconds
        //   - Buggy estimate: 10000 / 0.083 = ~120,000 seconds (33 hours!)

        if (stats.EstimatedTimeToCapacity.HasValue)
        {
            var buggyEstimate = stats.EstimatedTimeToCapacity.Value.TotalSeconds;
            var actualTokenRate = (5 * 16000) / 60.0;
            var realEstimate = stats.AvailableTokens / actualTokenRate;

            Console.WriteLine($"\nBuggy estimate: {buggyEstimate:F0} seconds ({buggyEstimate/60:F0} minutes)");
            Console.WriteLine($"Real estimate should be: {realEstimate:F0} seconds");
            Console.WriteLine($"Off by factor of: {buggyEstimate/realEstimate:F0}x");

            // The monitoring data is completely useless for capacity planning
            (buggyEstimate / realEstimate).Should().BeGreaterThan(100,
                "because the buggy formula makes the estimate wildly inaccurate");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EstimatedTimeToCapacity_ReturnsNull_WhenNoRequests()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        var stats = gate.GetUsageStats();

        // Assert
        // With RequestsInLastMinute = 0, the property returns null (line 42-43)
        stats.EstimatedTimeToCapacity.Should().BeNull(
            "because there are no requests to base the estimate on");

        // This part is correct - returning null when no data
        // But when there IS data, the calculation is wrong

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task EstimatedTimeToCapacity_ReturnsNull_WhenNoAvailableTokens()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 10000;
        options.SafetyBuffer = 1000;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act - Fill capacity
        var blocker = await gate.ReserveTokensAsync(inputTokens: 9000, estimatedOutputTokens: 0);

        var stats = gate.GetUsageStats();

        // Assert
        // With AvailableTokens <= 0, the property returns null (line 42)
        stats.EstimatedTimeToCapacity.Should().BeNull(
            "because there are no available tokens");

        // This part is also correct
        // The bug is specifically in the calculation formula at line 46

        await blocker.DisposeAsync();

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task FixedFormula_ShouldTrackActualTokenConsumption()
    {
        // This test documents what the CORRECT implementation should be

        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = 100000;
        options.SafetyBuffer = 10000;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        var totalTokens = 0;
        for (int i = 0; i < 10; i++)
        {
            var r = await gate.ReserveTokensAsync(inputTokens: 2000, estimatedOutputTokens: 2000);
            r.RecordActualUsage(4000);
            totalTokens += 4000;
            await r.DisposeAsync();
        }

        var stats = gate.GetUsageStats();

        // Expected correct implementation:
        // 1. Track total actual tokens consumed in the window
        // 2. Calculate: actualTokensPerSecond = totalTokensConsumed / windowDuration
        // 3. Estimate: secondsToCapacity = availableTokens / actualTokensPerSecond

        var windowSeconds = 60;
        var correctTokensPerSecond = totalTokens / (double)windowSeconds;
        var correctEstimate = stats.AvailableTokens / correctTokensPerSecond;

        Console.WriteLine("=== Correct Implementation ===");
        Console.WriteLine($"Total tokens consumed: {totalTokens:N0}");
        Console.WriteLine($"Window duration: {windowSeconds}s");
        Console.WriteLine($"Correct tokens per second: {correctTokensPerSecond:F2}");
        Console.WriteLine($"Available tokens: {stats.AvailableTokens:N0}");
        Console.WriteLine($"Correct estimate to capacity: {correctEstimate:F2} seconds");

        Console.WriteLine("\n=== Buggy Implementation ===");
        var buggyTokensPerSecond = stats.RequestsInLastMinute / 60.0;
        var buggyEstimate = stats.EstimatedTimeToCapacity?.TotalSeconds ?? 0;
        Console.WriteLine($"Buggy 'tokens' per second: {buggyTokensPerSecond:F2}");
        Console.WriteLine($"Buggy estimate: {buggyEstimate:F2} seconds");

        Console.WriteLine($"\nError: {buggyEstimate / correctEstimate:F0}x off!");

        // Cleanup
        gate.Dispose();
    }
}
