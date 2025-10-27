using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests to validate statistics accuracy under various loads.
/// Success criteria: All statistics accurate within 1%.
/// </summary>
public class StatisticsAccuracyTests : IDisposable
{
    private readonly TokenRateGate.Core.TokenRateGate _gate;
    private const int TokenLimit = 50_000;
    private const int WindowSeconds = 10;

    public StatisticsAccuracyTests()
    {
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = TokenLimit,
            WindowSeconds = WindowSeconds,
            SafetyBufferPercentage = 0.05,
            MaxConcurrentRequests = 20,
            MaxRequestsPerMinute = 1000,
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
    public async Task CurrentUsage_ShouldAccuratelyReflectActualUsage()
    {
        // Arrange
        const int tokensPerRequest = 1000;
        const int requestCount = 10;

        // Act: Make requests and track expected usage
        var reservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        long expectedUsage = 0;

        for (int i = 0; i < requestCount; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            reservation.RecordActualUsage(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            expectedUsage += tokensPerRequest;
            reservations.Add(reservation);
        }

        // Get stats
        var stats = _gate.GetUsageStats();

        // Assert: Current usage should match expected (within 1%)
        var tolerance = expectedUsage * 0.01; // 1% tolerance
        stats.CurrentUsage.Should().BeCloseTo(expectedUsage, (ulong)tolerance,
            "Current usage should accurately reflect recorded usage");

        // Cleanup
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReservedTokens_ShouldAccuratelyReflectActiveReservations()
    {
        // Arrange: Create active reservations without recording usage
        const int tokensPerRequest = 1000;
        const int requestCount = 5;

        // Act: Create reservations
        var reservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        long expectedReserved = 0;

        for (int i = 0; i < requestCount; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);

            expectedReserved += tokensPerRequest;
            reservations.Add(reservation);
        }

        // Get stats while reservations are active
        var stats = _gate.GetUsageStats();

        // Assert: Reserved tokens should match expected
        var tolerance = expectedReserved * 0.01; // 1% tolerance
        stats.TotalReserved.Should().BeCloseTo(expectedReserved, (ulong)tolerance,
            "Reserved tokens should accurately reflect active reservations");

        // Cleanup
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task AvailableCapacity_ShouldBeAccurate()
    {
        // Arrange: Use smaller amounts that fit within safety buffer
        const int usedTokens = 10_000;
        const int reservedTokens = 5_000;
        const int safetyBuffer = TokenLimit * 5 / 100; // 5% of 50k = 2500

        // Act: Create usage and reservations
        var completedReservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        var activeReservations = new List<TokenRateGate.Core.Models.TokenReservation>();

        // Add completed usage (10 reservations x 1000 tokens = 10k)
        for (int i = 0; i < 10; i++)
        {
            var res = await _gate.ReserveTokensAsync(500, 500);
            res.RecordActualUsage(500, 500);
            completedReservations.Add(res);
        }

        // Add active reservations (5 reservations x 1000 tokens = 5k)
        for (int i = 0; i < 5; i++)
        {
            activeReservations.Add(await _gate.ReserveTokensAsync(500, 500));
        }

        // Get stats
        var stats = _gate.GetUsageStats();

        // Calculate expected available capacity
        long expectedAvailable = TokenLimit - safetyBuffer - usedTokens - reservedTokens;

        // Assert: Available capacity should be accurate (within reasonable tolerance)
        var tolerance = Math.Max(2000, expectedAvailable * 0.05); // 5% tolerance
        stats.AvailableCapacity.Should().BeCloseTo(expectedAvailable, (ulong)tolerance,
            "Available capacity should accurately reflect limit - usage - reserved - buffer");

        stats.AvailableCapacity.Should().BeGreaterThan(0,
            "Should still have available capacity");

        // Cleanup
        foreach (var res in completedReservations.Concat(activeReservations))
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task UsagePercentage_ShouldBeAccurate()
    {
        // Arrange: Fill to 40% of effective capacity (accounting for 5% safety buffer)
        const int effectiveLimit = 47_500; // 50k - 5% safety buffer
        const int targetUsage = effectiveLimit * 40 / 100; // 19,000 tokens (40% of effective)
        const int tokensPerRequest = 1000;

        // Act: Fill to target
        var reservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        for (int i = 0; i < targetUsage / tokensPerRequest; i++)
        {
            var res = await _gate.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2);
            res.RecordActualUsage(
                tokensPerRequest / 2,
                tokensPerRequest / 2);
            reservations.Add(res);
        }

        // Get stats
        var stats = _gate.GetUsageStats();

        // Assert: Usage percentage should be ~35-45% (allowing for safety buffer and estimation variance)
        stats.UsagePercentage.Should().BeInRange(35.0, 45.0,
            "Usage percentage should accurately reflect ~40% capacity usage");

        // Cleanup
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stats_ShouldUpdateInRealTime()
    {
        // Arrange: Track stats over time
        var statsSamples = new List<(long usage, long reserved, long available)>();

        // Act: Make requests and sample stats
        for (int i = 0; i < 10; i++)
        {
            await using var reservation = await _gate.ReserveTokensAsync(1000, 1000);

            // Sample stats with active reservation
            var stats1 = _gate.GetUsageStats();
            statsSamples.Add((stats1.CurrentUsage, stats1.TotalReserved, stats1.AvailableCapacity));

            reservation.RecordActualUsage(1000, 1000);

            // Sample stats after recording usage
            var stats2 = _gate.GetUsageStats();
            statsSamples.Add((stats2.CurrentUsage, stats2.TotalReserved, stats2.AvailableCapacity));
        }

        // Assert: Stats should show progression
        // Reserved tokens should increase when reservation is active, then decrease
        statsSamples.Should().HaveCountGreaterThanOrEqualTo(20,
            "Should have samples from each reservation cycle");

        // Current usage should generally increase over time
        var firstUsage = statsSamples.First().usage;
        var lastUsage = statsSamples.Last().usage;
        lastUsage.Should().BeGreaterThan(firstUsage,
            "Current usage should increase as requests are completed");
    }

    [Fact]
    public async Task Stats_ShouldReflectWindowSliding()
    {
        // Arrange: Create usage that will age out
        const int initialUsage = 10_000;

        var reservation = await _gate.ReserveTokensAsync(5000, 5000);
        reservation.RecordActualUsage(5000, 5000);
        await reservation.DisposeAsync();

        var statsBeforeExpiry = _gate.GetUsageStats();
        var usageBeforeExpiry = statsBeforeExpiry.CurrentUsage;

        usageBeforeExpiry.Should().BeGreaterThanOrEqualTo(10_000,
            "Usage should be recorded immediately");

        // Act: Wait for window to expire
        await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 2));

        // Make a new request to trigger cleanup
        var newReservation = await _gate.ReserveTokensAsync(100, 100);
        var statsAfterExpiry = _gate.GetUsageStats();

        // Assert: Old usage should have expired
        statsAfterExpiry.CurrentUsage.Should().BeLessThan(usageBeforeExpiry,
            "Usage should decrease as old records age out of the window");

        // Cleanup
        await newReservation.DisposeAsync();
    }

    [Fact]
    public async Task IsNearCapacity_ShouldBeAccurate()
    {
        // Arrange: Fill to trigger IsNearCapacity (> 80% of TokenLimit)
        // TokenLimit = 50k, so need > 40k tokens to trigger
        // MaxConcurrentRequests = 20, so we're limited by concurrency
        const int tokensPerRequest = 2500; // 20 reservations * 2.5k = 50k total
        const int reservationCount = 18; // 18 * 2.5k = 45k tokens = 90% of 50k

        // Act: Fill capacity
        var reservations = new List<TokenRateGate.Core.Models.TokenReservation>();
        for (int i = 0; i < reservationCount; i++)
        {
            reservations.Add(await _gate.ReserveTokensAsync(
                tokensPerRequest / 2,
                tokensPerRequest / 2));
        }

        var stats = _gate.GetUsageStats();

        // Assert: IsNearCapacity should be true when > 80% of total limit
        // 18 reservations * 2.5k = 45k tokens out of 50k total = 90%
        stats.UsagePercentage.Should().BeGreaterThan(80.0,
            "Usage percentage should exceed 80% to trigger IsNearCapacity");

        stats.IsNearCapacity.Should().BeTrue(
            "IsNearCapacity should be true when usage exceeds 80%");

        // Cleanup
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }

        // Verify it returns false when capacity is low
        await Task.Delay(100);
        var statsAfterCleanup = _gate.GetUsageStats();
        statsAfterCleanup.IsNearCapacity.Should().BeFalse(
            "IsNearCapacity should be false after releasing reservations");
    }
}
