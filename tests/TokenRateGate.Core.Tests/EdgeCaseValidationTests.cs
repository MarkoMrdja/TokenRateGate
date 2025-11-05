using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;
using Xunit;

namespace TokenRateGate.Core.Tests;

/// <summary>
/// Tests for edge cases and boundary conditions to ensure robust handling of unusual inputs.
/// These tests validate behavior at system limits and with invalid configurations.
/// </summary>
public class EdgeCaseValidationTests
{
    #region Zero/Negative Token Values

    [Fact]
    public async Task ReserveTokensAsync_ShouldThrow_WhenInputTokensIsZero()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await gate.ReserveTokensAsync(0, 100));

        exception.ParamName.Should().Be("inputTokens");
        exception.Message.Should().Contain("must be positive");
    }

    [Fact]
    public async Task ReserveTokensAsync_ShouldThrow_WhenInputTokensIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await gate.ReserveTokensAsync(-100, 100));

        exception.ParamName.Should().Be("inputTokens");
        exception.Message.Should().Contain("must be positive");
    }

    [Fact]
    public async Task ReserveTokensAsync_ShouldThrow_WhenOutputTokensIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await gate.ReserveTokensAsync(100, -100));

        exception.ParamName.Should().Be("estimatedOutputTokens");
        exception.Message.Should().Contain("cannot be negative");
    }

    [Fact]
    public async Task ReserveTokensAsync_ShouldAllow_ExplicitZeroOutputTokens()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - explicitly request 0 output tokens
        await using var reservation = await gate.ReserveTokensAsync(100, 0);

        // Assert
        reservation.Should().NotBeNull();
        reservation.ReservedTokens.Should().Be(100); // Only input tokens
    }

    [Fact]
    public async Task ReserveTokensAsync_ShouldUseEstimationStrategy_WhenOutputTokensNotProvided()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5 // 50% of input
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - don't provide output tokens (null)
        await using var reservation = await gate.ReserveTokensAsync(1000, null);

        // Assert - should estimate 1000 + (1000 * 0.5) = 1500
        reservation.Should().NotBeNull();
        reservation.ReservedTokens.Should().Be(1500);
    }

    [Fact]
    public void RecordActualUsage_ShouldThrow_WhenTotalTokensIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = gate.ReserveTokensAsync(1000, 500).Result;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => reservation.RecordActualUsage(-100));

        exception.ParamName.Should().Be("totalActualTokens");
        exception.Message.Should().Contain("cannot be negative");

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void RecordActualUsage_ShouldThrow_WhenInputTokensIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = gate.ReserveTokensAsync(1000, 500).Result;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => reservation.RecordActualUsage(-100, 500));

        exception.ParamName.Should().Be("actualInputTokens");
        exception.Message.Should().Contain("cannot be negative");

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void RecordActualUsage_ShouldThrow_WhenOutputTokensIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = gate.ReserveTokensAsync(1000, 500).Result;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => reservation.RecordActualUsage(1000, -500));

        exception.ParamName.Should().Be("actualOutputTokens");
        exception.Message.Should().Contain("cannot be negative");

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public async Task RecordActualUsage_ShouldAllow_ActualExceedingReservation()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - reserve 1500, but actually use 2000
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);
        reservation.RecordActualUsage(1500, 500); // 2000 total vs 1500 reserved

        // Assert - should be allowed (over-estimation is allowed, just inefficient)
        reservation.ActualTokensUsed.Should().Be(2000);
    }

    #endregion

    #region Integer Overflow Scenarios

    [Fact]
    public async Task ReserveTokensAsync_ShouldThrow_WhenTotalExceedsEffectiveCapacity()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.1 // Effective capacity = 9000
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act & Assert - request more than effective capacity
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gate.ReserveTokensAsync(6000, 4000)); // 10,000 > 9,000

        exception.Message.Should().Contain("exceeds effective capacity");
        exception.Message.Should().MatchRegex(@"10[.,]000"); // Support both comma and period as thousand separator
        exception.Message.Should().MatchRegex(@"9[.,]000");  // Culture-invariant check
    }

    [Fact]
    public async Task ReserveTokensAsync_ShouldThrow_WhenRequestingIntMaxValue()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = int.MaxValue,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act & Assert - int.MaxValue input + int.MaxValue output would overflow
        // The implementation catches this and throws ArgumentException
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await gate.ReserveTokensAsync(int.MaxValue, int.MaxValue));

        exception.Message.Should().Contain("exceeds maximum allowed value");
    }

    [Fact]
    public void SafetyBuffer_ShouldNotOverflow_WithLargeTokenLimit()
    {
        // Arrange & Act - large token limit with safety buffer
        var options = new TokenRateGateOptions
        {
            TokenLimit = 1_000_000_000, // 1 billion
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.1 // 10% = 100 million
        };

        // Act - creating the gate should not overflow when calculating safety buffer
        Action act = () =>
        {
            using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
            var stats = gate.GetUsageStats();

            // Assert - safety buffer should be calculated correctly
            stats.TokenLimit.Should().Be(1_000_000_000);
            stats.EffectiveCapacity.Should().Be(900_000_000);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SafetyBuffer_ShouldNotOverflow_WithIntMaxTokenLimit()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = int.MaxValue,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.05 // 5%
        };

        // Act - creating gate with int.MaxValue should handle safety buffer calculation
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var stats = gate.GetUsageStats();

        // Assert - effective capacity should be calculated without overflow
        stats.TokenLimit.Should().Be(int.MaxValue);
        stats.EffectiveCapacity.Should().BeLessThan(int.MaxValue);
        stats.EffectiveCapacity.Should().BeGreaterThan(0);

        // Should be able to reserve tokens
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);
        reservation.Should().NotBeNull();
    }

    #endregion

    #region Configuration Edge Cases

    [Fact]
    public void Constructor_ShouldThrow_WhenTokenLimitIsZero()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 0,
            WindowSeconds = 60
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("TokenLimit must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenTokenLimitIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = -1000,
            WindowSeconds = 60
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("TokenLimit must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenWindowSecondsIsZero()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 0
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("WindowSeconds must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenWindowSecondsIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = -60
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("WindowSeconds must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenSafetyBufferPercentageIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = -0.1
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("SafetyBufferPercentage cannot be negative");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenSafetyBufferPercentageExceedsOne()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 1.5 // 150% - invalid
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("SafetyBufferPercentage must be less than 1.0");
    }

    [Fact]
    public void SafetyBufferPercentage_FullBuffer_ShouldThrowOnConstruction()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 1.0 // 100% buffer - validation prevents this
        };

        // Act & Assert - constructor should throw before we can even check stats
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("SafetyBufferPercentage must be less than 1.0");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenMaxConcurrentRequestsIsZero()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 0
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("MaxConcurrentRequests must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenMaxConcurrentRequestsIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = -10
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("MaxConcurrentRequests must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenMaxConcurrentRequestsExceedsLimit()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxConcurrentRequests = 20_000 // Exceeds max of 10,000
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("exceeds safe limit");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenMaxRequestsPerMinuteIsZero()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 0
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("MaxRequestsPerMinute must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenRequestWindowSecondsIsZero()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            RequestWindowSeconds = 0
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("RequestWindowSeconds must be positive");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenRequestWindowSecondsIsNegative()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            RequestWindowSeconds = -30
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("RequestWindowSeconds must be positive");
    }

    [Fact]
    public void OutputMultiplier_Zero_ShouldWork()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.0 // 0% of input = 0 output
        };

        // Act - should not throw
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = gate.ReserveTokensAsync(1000, null).Result; // null = use estimation

        // Assert - should only reserve input tokens (1000 + 0)
        reservation.ReservedTokens.Should().Be(1000);

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void OutputMultiplier_Negative_ShouldThrow()
    {
        // Arrange - negative multiplier is not allowed
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = -0.5 // Negative is not valid
        };

        // Act & Assert - should throw during construction
        var exception = Assert.Throws<ArgumentException>(
            () => new TokenRateGate(options, NullLogger<TokenRateGate>.Instance));

        exception.Message.Should().Contain("OutputMultiplier cannot be negative");
    }

    [Fact]
    public void DefaultOutputTokens_Zero_ShouldWork()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount,
            DefaultOutputTokens = 0 // Add 0 tokens
        };

        // Act
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = gate.ReserveTokensAsync(1000, null).Result;

        // Assert - should only reserve input tokens
        reservation.ReservedTokens.Should().Be(1000);

        // Cleanup
        reservation.DisposeAsync().AsTask().Wait();
    }

    #endregion

    #region Reservation Lifecycle Edge Cases

    [Fact]
    public async Task RecordActualUsage_ShouldBeIdempotent_WhenCalledMultipleTimes()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - call multiple times with different values
        reservation.RecordActualUsage(1200);
        reservation.RecordActualUsage(1500); // Should be ignored
        reservation.RecordActualUsage(1800); // Should be ignored

        // Assert - only first call should be recorded
        reservation.ActualTokensUsed.Should().Be(1200);
    }

    [Fact]
    public async Task RecordActualUsage_TwoParameter_ShouldBeIdempotent()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - call multiple times with different values
        reservation.RecordActualUsage(1000, 200); // 1200 total
        reservation.RecordActualUsage(1000, 500); // Should be ignored
        reservation.RecordActualUsage(1000, 800); // Should be ignored

        // Assert - only first call should be recorded
        reservation.ActualTokensUsed.Should().Be(1200);
    }

    [Fact]
    public async Task RecordActualUsage_AfterDisposal_ShouldBeAllowed()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - dispose first, then record usage
        await reservation.DisposeAsync();
        reservation.RecordActualUsage(1200);

        // Assert - should be allowed (though unusual pattern)
        reservation.ActualTokensUsed.Should().Be(1200);
    }

    [Fact]
    public async Task Disposal_WithoutRecordingUsage_ShouldBeAllowed()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - create reservation and dispose without recording usage
        var reservation = await gate.ReserveTokensAsync(1000, 500);
        await reservation.DisposeAsync();

        // Assert - should not throw, usage should be null
        reservation.ActualTokensUsed.Should().BeNull();

        // Capacity should be freed
        var stats = gate.GetUsageStats();
        stats.ActiveReservationsCount.Should().Be(0);
        stats.TotalReserved.Should().Be(0);
    }

    [Fact]
    public async Task Disposal_ShouldBeIdempotent_WhenCalledMultipleTimes()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - dispose multiple times
        await reservation.DisposeAsync();
        await reservation.DisposeAsync();
        await reservation.DisposeAsync();

        // Assert - should not throw or cause issues
        var stats = gate.GetUsageStats();
        stats.ActiveReservationsCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentDisposal_ShouldBeThreadSafe()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - dispose from multiple threads concurrently
        var disposalTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => await reservation.DisposeAsync()))
            .ToArray();

        // Assert - should not throw
        var act = async () => await Task.WhenAll(disposalTasks);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ConcurrentRecordActualUsage_ShouldBeThreadSafe()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - record from multiple threads concurrently with different values
        var recordTasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => reservation.RecordActualUsage(1000 + i * 100)))
            .ToArray();

        // Assert - should not throw
        var act = async () => await Task.WhenAll(recordTasks);
        await act.Should().NotThrowAsync();

        // Only one value should be recorded (whichever thread won the race)
        reservation.ActualTokensUsed.Should().NotBeNull();
        reservation.ActualTokensUsed.Should().BeInRange(1000, 1900);
    }

    [Fact]
    public async Task RecordActualUsage_WithZeroTokens_ShouldBeAllowed()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);
        await using var reservation = await gate.ReserveTokensAsync(1000, 500);

        // Act - record 0 actual tokens (unusual but valid)
        reservation.RecordActualUsage(0, 0);

        // Assert
        reservation.ActualTokensUsed.Should().Be(0);
    }

    #endregion

    #region Mixed Edge Cases

    [Fact]
    public async Task SmallTokenLimit_WithLargeSafetyBuffer_ShouldWorkCorrectly()
    {
        // Arrange - very small limits to test edge of system behavior
        // Note: Need to reduce DefaultOutputTokens or it will exceed TokenLimit
        var options = new TokenRateGateOptions
        {
            TokenLimit = 2000, // Increased to accommodate default output tokens
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.5, // 50% buffer, only 1000 tokens effective
            DefaultOutputTokens = 100 // Reduced from default 1000
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act
        await using var reservation = await gate.ReserveTokensAsync(250, 200); // 450 tokens

        // Assert
        var stats = gate.GetUsageStats();
        stats.TokenLimit.Should().Be(2000);
        stats.EffectiveCapacity.Should().Be(1000);
        stats.TotalReserved.Should().Be(450);
        stats.AvailableTokens.Should().Be(550);
    }

    [Fact]
    public async Task VeryShortWindow_ShouldExpireQuickly()
    {
        // Arrange - 1 second window
        var options = new TokenRateGateOptions
        {
            TokenLimit = 1000,
            WindowSeconds = 1,
            SafetyBufferPercentage = 0.0
        };
        using var gate = new TokenRateGate(options, NullLogger<TokenRateGate>.Instance);

        // Act - use tokens
        await using (var r1 = await gate.ReserveTokensAsync(500, 400))
        {
            r1.RecordActualUsage(900);
        }

        var statsImmediately = gate.GetUsageStats();

        // Wait for window to expire (plus cleanup interval)
        await Task.Delay(TimeSpan.FromSeconds(4));

        var statsAfterExpiry = gate.GetUsageStats();

        // Assert - usage should have decreased
        statsImmediately.CurrentUsage.Should().Be(900);
        statsAfterExpiry.CurrentUsage.Should().BeLessThan(statsImmediately.CurrentUsage);
    }

    #endregion
}
