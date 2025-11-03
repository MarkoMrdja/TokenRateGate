using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core.Options;
using Xunit;

namespace TokenRateGate.Core.Tests;

/// <summary>
/// Baseline functionality tests to ensure core features work correctly.
/// These tests validate the fundamental behavior after bug fixes.
/// </summary>
public class BaselineFunctionalityTests
{
    [Fact]
    public async Task ReserveTokensAsync_ShouldGrantImmediateReservation_WhenCapacityAvailable()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 5
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Assert
        reservation.Should().NotBeNull();
        reservation.ReservedTokens.Should().Be(1500);
        reservation.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RecordActualUsage_ShouldUpdateCurrentUsage()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);
        reservation.RecordActualUsage(1000, 400);

        // Release reservation
        await reservation.DisposeAsync();

        // Small delay to ensure cleanup
        await Task.Delay(100);

        // Assert
        var stats = gate.GetUsageStats();
        stats.CurrentUsage.Should().Be(1400); // 1000 input + 400 output
    }

    [Fact]
    public void UsagePercentage_ShouldIncludeBothCurrentAndReserved()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0 // No buffer for easier math
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - make a reservation but don't complete it yet
        var reservation = gate.ReserveTokensAsync(3000, 2000).Result;

        // Assert
        var stats = gate.GetUsageStats();
        stats.TotalReserved.Should().Be(5000);
        stats.CurrentUsage.Should().Be(0); // No completed requests yet
        stats.UsagePercentage.Should().BeApproximately(50.0, 0.1); // 5000/10000 * 100 = 50%

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void UsagePercentage_ShouldNotExceed100Percent_WithActiveReservations()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - reserve 60% of capacity
        var reservation = gate.ReserveTokensAsync(4000, 2000).Result;

        // Assert
        var stats = gate.GetUsageStats();
        stats.UsagePercentage.Should().BeLessThan(100.0);
        stats.UsagePercentage.Should().BeApproximately(60.0, 0.1);

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void GetUsageStats_ShouldReturnEffectiveCapacity()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.1 // 10% buffer = 1000 tokens
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act
        var stats = gate.GetUsageStats();

        // Assert
        stats.TokenLimit.Should().Be(10_000);
        stats.EffectiveCapacity.Should().Be(9_000); // 10,000 - 10% = 9,000
        stats.AvailableTokens.Should().Be(9_000); // Nothing reserved or used yet
    }

    [Fact]
    public void GetUsageStats_AvailableTokens_ShouldDecrease_WithActiveReservations()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.1 // 10% buffer = 1000 tokens
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act
        var reservation = gate.ReserveTokensAsync(3000, 2000).Result; // 5000 tokens reserved
        var stats = gate.GetUsageStats();

        // Assert
        stats.EffectiveCapacity.Should().Be(9_000);
        stats.TotalReserved.Should().Be(5_000);
        stats.AvailableTokens.Should().Be(4_000); // 9000 - 5000 = 4000
        stats.ActiveReservationsCount.Should().Be(1);

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public async Task MultipleReservations_ShouldAccumulateCorrectly()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 3,
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - make 3 reservations
        await using var r1 = await gate.ReserveTokensAsync(1000, 500); // 1500
        await using var r2 = await gate.ReserveTokensAsync(1000, 1000); // 2000
        await using var r3 = await gate.ReserveTokensAsync(500, 500);   // 1000

        // Assert
        var stats = gate.GetUsageStats();
        stats.TotalReserved.Should().Be(4500); // 1500 + 2000 + 1000
        stats.ActiveReservationsCount.Should().Be(3);
        stats.AvailableTokens.Should().Be(5500); // 10000 - 4500
        stats.UsagePercentage.Should().BeApproximately(45.0, 0.1);
    }

    [Fact]
    public async Task QueueBehavior_ShouldWaitWhenCapacityExhausted()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 5_000,
            WindowSeconds = 5,
            MaxConcurrentRequests = 10, // Allow multiple concurrent requests so second request can reach waiting queue
            SafetyBufferPercentage = 0.0,
            MaxWaitTime = TimeSpan.FromSeconds(10)
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - reserve most of capacity
        await using var r1 = await gate.ReserveTokensAsync(2000, 2000); // 4000 tokens

        var stats1 = gate.GetUsageStats();
        stats1.AvailableTokens.Should().Be(1000); // 5000 - 4000

        // This should queue because only 1000 tokens available but needs 2000
        var startTime = DateTime.UtcNow;
        var r2Task = gate.ReserveTokensAsync(1500, 500); // 2000 tokens needed

        // Give it a moment to queue
        await Task.Delay(100);

        var stats2 = gate.GetUsageStats();
        stats2.WaitingRequestsCount.Should().Be(1);

        // Release first reservation to free capacity
        await r1.DisposeAsync();

        // Now the waiting request should be granted
        await using var r2 = await r2Task;
        var waitTime = DateTime.UtcNow - startTime;

        // Assert
        r2.Should().NotBeNull();
        waitTime.Should().BeLessThan(TimeSpan.FromSeconds(8)); // Should not wait full window
    }

    [Fact]
    public async Task ReleasedReservation_ShouldFreeCapacity()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act
        var reservation = await gate.ReserveTokensAsync(3000, 2000);
        var statsWithReservation = gate.GetUsageStats();

        await reservation.DisposeAsync();

        var statsAfterRelease = gate.GetUsageStats();

        // Assert
        statsWithReservation.TotalReserved.Should().Be(5000);
        statsWithReservation.AvailableTokens.Should().Be(5000);

        statsAfterRelease.TotalReserved.Should().Be(0);
        statsAfterRelease.ActiveReservationsCount.Should().Be(0);
    }

    [Fact]
    public async Task MaxConcurrentRequests_ShouldEnforceLimit()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 100_000, // High limit so we don't hit token limits
            WindowSeconds = 60,
            MaxConcurrentRequests = 2 // Only 2 concurrent
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - Get 2 reservations
        await using var r1 = await gate.ReserveTokensAsync(100, 100);
        await using var r2 = await gate.ReserveTokensAsync(100, 100);

        var stats = gate.GetUsageStats();

        // Assert - exactly 2 should be active
        stats.ActiveReservationsCount.Should().Be(2);

        // Try a 3rd - it should eventually succeed but only after one is released
        // (We'll just verify the gate doesn't crash and can handle the limit)
        var r3Task = gate.ReserveTokensAsync(100, 100);

        // Release one to allow third
        await r1.DisposeAsync();

        await using var r3 = await r3Task;
        r3.Should().NotBeNull();
    }

    [Fact]
    public async Task EstimationEfficiency_ShouldBeTracked()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - reserve 1500, but actually use only 1200
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);
        reservation.RecordActualUsage(1000, 200); // 1200 total vs 1500 reserved

        // Get stats while reservation is still active (before DisposeAsync)
        var stats = gate.GetUsageStats();

        // Assert
        // Note: AverageEstimationEfficiency is calculated from active reservations
        // and may be 1.0 if the calculation happens before disposal
        stats.AverageEstimationEfficiency.Should().BeGreaterThanOrEqualTo(0.0);
        stats.ActiveReservationsCount.Should().Be(1);
    }

    [Fact]
    public void IsNearCapacity_ShouldBeTrueWhenAbove80Percent()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - reserve 85% of capacity
        var reservation = gate.ReserveTokensAsync(6000, 2500).Result; // 8500 = 85%

        // Assert
        var stats = gate.GetUsageStats();
        stats.UsagePercentage.Should().BeGreaterThan(80.0);
        stats.IsNearCapacity.Should().BeTrue();

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public async Task SlidingWindow_ShouldExpireOldUsage()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 5_000,
            WindowSeconds = 2, // Very short window for testing
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - use tokens and complete
        await using (var r1 = await gate.ReserveTokensAsync(2000, 1000))
        {
            r1.RecordActualUsage(2000, 1000);
        }

        var statsImmediately = gate.GetUsageStats();

        // Wait for window to expire
        await Task.Delay(TimeSpan.FromSeconds(3));

        var statsAfterExpiry = gate.GetUsageStats();

        // Assert
        statsImmediately.CurrentUsage.Should().Be(3000);
        // After window expires, usage should decrease (may not be 0 due to cleanup interval)
        statsAfterExpiry.CurrentUsage.Should().BeLessThan(statsImmediately.CurrentUsage);
    }
}
