using FluentAssertions;
using TokenRateGate.Core.Capacity;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using Xunit;

namespace TokenRateGate.Core.Tests;

public class CapacityCalculatorTests
{
    private readonly TokenRateGateOptions _defaultOptions = new()
    {
        TokenLimit = 1000,
        WindowSeconds = 60,
        MaxRequestsPerMinute = 100,
        SafetyBufferPercentage = 0.1
    };

    private CapacityCalculator CreateCalculator(int? safetyBuffer = null)
    {
        var buffer = safetyBuffer ?? (int)(_defaultOptions.TokenLimit * _defaultOptions.SafetyBufferPercentage);
        return new CapacityCalculator(_defaultOptions, buffer);
    }

    #region CalculateCurrentUsage Tests

    [Fact]
    public void CalculateCurrentUsage_WithNoActiveReservations_ReturnsCompletedUsageOnly()
    {
        // Arrange
        var calculator = CreateCalculator();
        long completedUsage = 500;
        var activeReservations = new List<PendingReservations>();

        // Act
        var result = calculator.CalculateCurrentUsage(completedUsage, activeReservations);

        // Assert
        result.Should().Be(500);
    }

    [Fact]
    public void CalculateCurrentUsage_WithEstimatedTokens_UsesEstimated()
    {
        // Arrange
        var calculator = CreateCalculator();
        long completedUsage = 500;
        var activeReservations = new List<PendingReservations>
        {
            new(Guid.NewGuid(), 100, DateTime.UtcNow),
            new(Guid.NewGuid(), 200, DateTime.UtcNow)
        };

        // Act
        var result = calculator.CalculateCurrentUsage(completedUsage, activeReservations);

        // Assert
        result.Should().Be(800); // 500 + 100 + 200
    }

    [Fact]
    public void CalculateCurrentUsage_WithActualTokens_UsesActual()
    {
        // Arrange
        var calculator = CreateCalculator();
        long completedUsage = 500;
        var activeReservations = new List<PendingReservations>
        {
            new(Guid.NewGuid(), 100, DateTime.UtcNow) { ActualTokensUsed = 80 },
            new(Guid.NewGuid(), 200, DateTime.UtcNow) { ActualTokensUsed = 150 }
        };

        // Act
        var result = calculator.CalculateCurrentUsage(completedUsage, activeReservations);

        // Assert
        result.Should().Be(730); // 500 + 80 + 150
    }

    [Fact]
    public void CalculateCurrentUsage_WithMixedActualAndEstimated_UsesBoth()
    {
        // Arrange
        var calculator = CreateCalculator();
        long completedUsage = 500;
        var activeReservations = new List<PendingReservations>
        {
            new(Guid.NewGuid(), 100, DateTime.UtcNow) { ActualTokensUsed = 80 },
            new(Guid.NewGuid(), 200, DateTime.UtcNow) // No actual yet, uses estimated
        };

        // Act
        var result = calculator.CalculateCurrentUsage(completedUsage, activeReservations);

        // Assert
        result.Should().Be(780); // 500 + 80 + 200
    }

    #endregion

    #region CalculateReservedTokens Tests

    [Fact]
    public void CalculateReservedTokens_WithNoActiveReservations_ReturnsZero()
    {
        // Arrange
        var calculator = CreateCalculator();
        var activeReservations = new List<PendingReservations>();

        // Act
        var result = calculator.CalculateReservedTokens(activeReservations);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void CalculateReservedTokens_WithOnlyEstimated_ReturnsTotal()
    {
        // Arrange
        var calculator = CreateCalculator();
        var activeReservations = new List<PendingReservations>
        {
            new(Guid.NewGuid(), 100, DateTime.UtcNow),
            new(Guid.NewGuid(), 200, DateTime.UtcNow)
        };

        // Act
        var result = calculator.CalculateReservedTokens(activeReservations);

        // Assert
        result.Should().Be(300);
    }

    [Fact]
    public void CalculateReservedTokens_WithOnlyActual_ReturnsZero()
    {
        // Arrange
        var calculator = CreateCalculator();
        var activeReservations = new List<PendingReservations>
        {
            new(Guid.NewGuid(), 100, DateTime.UtcNow) { ActualTokensUsed = 80 },
            new(Guid.NewGuid(), 200, DateTime.UtcNow) { ActualTokensUsed = 150 }
        };

        // Act
        var result = calculator.CalculateReservedTokens(activeReservations);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void CalculateReservedTokens_WithMixedActualAndEstimated_ReturnsOnlyEstimated()
    {
        // Arrange
        var calculator = CreateCalculator();
        var activeReservations = new List<PendingReservations>
        {
            new(Guid.NewGuid(), 100, DateTime.UtcNow) { ActualTokensUsed = 80 },
            new(Guid.NewGuid(), 200, DateTime.UtcNow),  // Reserved
            new(Guid.NewGuid(), 300, DateTime.UtcNow)   // Reserved
        };

        // Act
        var result = calculator.CalculateReservedTokens(activeReservations);

        // Assert
        result.Should().Be(500); // 200 + 300
    }

    #endregion

    #region HasCapacity Tests

    [Fact]
    public void HasCapacity_WithAvailableTokenAndRequestCapacity_ReturnsTrue()
    {
        // Arrange
        var calculator = CreateCalculator();
        long currentUsage = 500;
        int currentRequestCount = 50;
        int requiredTokens = 300;

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Effective limit = 1000 - 100 (10% safety) = 900
        // 500 + 300 = 800 <= 900 ✓
        // 50 < 100 ✓
        result.Should().BeTrue();
    }

    [Fact]
    public void HasCapacity_AtExactTokenLimit_ReturnsTrue()
    {
        // Arrange
        var calculator = CreateCalculator();
        long currentUsage = 500;
        int currentRequestCount = 50;
        int requiredTokens = 400; // Exactly at limit

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Effective limit = 1000 - 100 = 900
        // 500 + 400 = 900 <= 900 ✓
        result.Should().BeTrue();
    }

    [Fact]
    public void HasCapacity_ExceedingTokenLimit_ReturnsFalse()
    {
        // Arrange
        var calculator = CreateCalculator();
        long currentUsage = 500;
        int currentRequestCount = 50;
        int requiredTokens = 401; // Exceeds limit by 1

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Effective limit = 1000 - 100 = 900
        // 500 + 401 = 901 > 900 ✗
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCapacity_ExceedingRequestLimit_ReturnsFalse()
    {
        // Arrange
        var calculator = CreateCalculator();
        long currentUsage = 100;
        int currentRequestCount = 100; // At limit
        int requiredTokens = 100;

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Token capacity available but request limit reached
        // 100 < 100 ✗ (not strictly less than)
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCapacity_BothLimitsExceeded_ReturnsFalse()
    {
        // Arrange
        var calculator = CreateCalculator();
        long currentUsage = 800;
        int currentRequestCount = 100;
        int requiredTokens = 200;

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Both limits exceeded
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCapacity_WithZeroSafetyBuffer_UsesFullLimit()
    {
        // Arrange
        var calculator = CreateCalculator(safetyBuffer: 0);
        long currentUsage = 900;
        int currentRequestCount = 50;
        int requiredTokens = 100;

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Effective limit = 1000 - 0 = 1000
        // 900 + 100 = 1000 <= 1000 ✓
        result.Should().BeTrue();
    }

    [Fact]
    public void HasCapacity_WithLargeSafetyBuffer_ReducesAvailableCapacity()
    {
        // Arrange
        var calculator = CreateCalculator(safetyBuffer: 500); // 50%
        long currentUsage = 400;
        int currentRequestCount = 50;
        int requiredTokens = 200;

        // Act
        var result = calculator.HasCapacity(currentUsage, currentRequestCount, requiredTokens);

        // Assert
        // Effective limit = 1000 - 500 = 500
        // 400 + 200 = 600 > 500 ✗
        result.Should().BeFalse();
    }

    #endregion

    #region GetEffectiveLimit Tests

    [Fact]
    public void GetEffectiveLimit_WithDefaultSafetyBuffer_ReturnsReducedLimit()
    {
        // Arrange
        var calculator = CreateCalculator(); // 10% safety buffer

        // Act
        var result = calculator.GetEffectiveLimit();

        // Assert
        result.Should().Be(900); // 1000 - 100
    }

    [Fact]
    public void GetEffectiveLimit_WithZeroSafetyBuffer_ReturnsFullLimit()
    {
        // Arrange
        var calculator = CreateCalculator(safetyBuffer: 0);

        // Act
        var result = calculator.GetEffectiveLimit();

        // Assert
        result.Should().Be(1000);
    }

    [Fact]
    public void GetEffectiveLimit_WithLargeSafetyBuffer_ReturnsSignificantlyReducedLimit()
    {
        // Arrange
        var calculator = CreateCalculator(safetyBuffer: 500);

        // Act
        var result = calculator.GetEffectiveLimit();

        // Assert
        result.Should().Be(500);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new CapacityCalculator(null!, 100);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    #endregion
}
