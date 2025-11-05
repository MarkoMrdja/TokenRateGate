using FluentAssertions;
using Microsoft.Extensions.Logging;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Tests.TestHelpers;
using TokenRateGate.Core.Timeline;
using Xunit;

namespace TokenRateGate.Core.Tests;

public class TokenTimelineManagerTests
{
    private readonly TestClock _clock;
    private readonly TestLogger<TokenTimelineManagerTests> _logger;
    private readonly TokenRateGateOptions _options;

    public TokenTimelineManagerTests()
    {
        _clock = new TestClock();
        _logger = new TestLogger<TokenTimelineManagerTests>();
        _options = new TokenRateGateOptions
        {
            TokenLimit = 1000,
            WindowSeconds = 60,
            RequestWindowSeconds = 60,
            MaxRequestsPerMinute = 100
        };
    }

    private TokenTimelineManager CreateManager() =>
        new TokenTimelineManager(_options, _clock, _logger);

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new TokenTimelineManager(null!, _clock, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Fact]
    public void Constructor_WithNullClock_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new TokenTimelineManager(_options, null!, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("clock");
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new TokenTimelineManager(_options, _clock, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void InitialState_ShouldBeEmpty()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        manager.CurrentActualUsage.Should().Be(0);
        manager.RequestCount.Should().Be(0);
        manager.TokenUsageCount.Should().Be(0);
    }

    [Fact]
    public void AddTokenUsage_IncrementsCurrentUsage()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.AddTokenUsage(actualTokens: 100, reservedTokens: 150);

        // Assert
        manager.CurrentActualUsage.Should().Be(100);
        manager.TokenUsageCount.Should().Be(1);
    }

    [Fact]
    public void AddTokenUsage_MultipleEntries_AccumulatesUsage()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.AddTokenUsage(100, 150);
        manager.AddTokenUsage(200, 250);
        manager.AddTokenUsage(300, 350);

        // Assert
        manager.CurrentActualUsage.Should().Be(600);
        manager.TokenUsageCount.Should().Be(3);
    }

    [Fact]
    public void AddRequest_IncrementsRequestCount()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.AddRequest();

        // Assert
        manager.RequestCount.Should().Be(1);
    }

    [Fact]
    public void AddRequest_MultipleRequests_AccumulatesCount()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.AddRequest();
        manager.AddRequest();
        manager.AddRequest();

        // Assert
        manager.RequestCount.Should().Be(3);
    }

    [Fact]
    public void CleanupExpiredEntries_WithNoExpiredTokens_ReturnsNoFreed()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(100, 150);
        manager.AddRequest();

        // Act - advance only 30 seconds (window is 60)
        _clock.Advance(TimeSpan.FromSeconds(30));
        var result = manager.CleanupExpiredEntries();

        // Assert
        result.TokensFreed.Should().BeFalse();
        result.RequestsFreed.Should().BeFalse();
        result.RemovedTokens.Should().Be(0);
        result.RemovedRequests.Should().Be(0);
        manager.CurrentActualUsage.Should().Be(100);
        manager.RequestCount.Should().Be(1);
    }

    [Fact]
    public void CleanupExpiredEntries_WithExpiredTokens_FreesCapacity()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(100, 150);
        manager.AddTokenUsage(200, 250);

        // Act - advance beyond window
        _clock.Advance(TimeSpan.FromSeconds(61));
        var result = manager.CleanupExpiredEntries();

        // Assert
        result.TokensFreed.Should().BeTrue();
        result.RemovedTokens.Should().Be(300); // Both entries removed
        manager.CurrentActualUsage.Should().Be(0);
        manager.TokenUsageCount.Should().Be(0);
    }

    [Fact]
    public void CleanupExpiredEntries_WithPartiallyExpiredTokens_FreesOnlyExpired()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(100, 150);

        _clock.Advance(TimeSpan.FromSeconds(30));
        manager.AddTokenUsage(200, 250);

        // Act - advance to make first entry expire (total 61 seconds from first entry)
        _clock.Advance(TimeSpan.FromSeconds(31));
        var result = manager.CleanupExpiredEntries();

        // Assert
        result.TokensFreed.Should().BeTrue();
        result.RemovedTokens.Should().Be(100); // Only first entry
        manager.CurrentActualUsage.Should().Be(200); // Second entry remains
        manager.TokenUsageCount.Should().Be(1);
    }

    [Fact]
    public void CleanupExpiredEntries_WithExpiredRequests_FreesRequests()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddRequest();
        manager.AddRequest();

        // Act - advance beyond request window
        _clock.Advance(TimeSpan.FromSeconds(61));
        var result = manager.CleanupExpiredEntries();

        // Assert
        result.RequestsFreed.Should().BeTrue();
        result.RemovedRequests.Should().Be(2);
        manager.RequestCount.Should().Be(0);
    }

    [Fact]
    public void CleanupExpiredEntries_WithPartiallyExpiredRequests_FreesOnlyExpired()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddRequest();

        _clock.Advance(TimeSpan.FromSeconds(30));
        manager.AddRequest();

        // Act - advance to make first request expire
        _clock.Advance(TimeSpan.FromSeconds(31));
        var result = manager.CleanupExpiredEntries();

        // Assert
        result.RequestsFreed.Should().BeTrue();
        result.RemovedRequests.Should().Be(1);
        manager.RequestCount.Should().Be(1);
    }

    [Fact]
    public void CleanupExpiredEntries_WithBothTokensAndRequestsExpired_FreesBoth()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(100, 150);
        manager.AddRequest();

        // Act - advance beyond both windows
        _clock.Advance(TimeSpan.FromSeconds(61));
        var result = manager.CleanupExpiredEntries();

        // Assert
        result.TokensFreed.Should().BeTrue();
        result.RequestsFreed.Should().BeTrue();
        result.RemovedTokens.Should().Be(100);
        result.RemovedRequests.Should().Be(1);
        manager.CurrentActualUsage.Should().Be(0);
        manager.RequestCount.Should().Be(0);
    }

    [Fact]
    public void CleanupStaleReservations_WithNullDictionary_ThrowsArgumentNullException()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        var act = () => manager.CleanupStaleReservations(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CleanupStaleReservations_WithNoStaleReservations_RemovesNothing()
    {
        // Arrange
        var manager = CreateManager();
        var reservations = new Dictionary<Guid, PendingReservations>
        {
            { Guid.NewGuid(), new PendingReservations(Guid.NewGuid(), 100, _clock.UtcNow) }
        };

        // Act
        var removedCount = manager.CleanupStaleReservations(reservations);

        // Assert
        removedCount.Should().Be(0);
        reservations.Should().HaveCount(1);
    }

    [Fact]
    public void CleanupStaleReservations_WithStaleReservation_RemovesIt()
    {
        // Arrange
        var manager = CreateManager();
        var staleId = Guid.NewGuid();
        var reservations = new Dictionary<Guid, PendingReservations>
        {
            { staleId, new PendingReservations(staleId, 100, _clock.UtcNow) }
        };

        // Act - advance time beyond stale timeout (10 minutes for 60s window)
        _clock.Advance(TimeSpan.FromMinutes(11));
        var removedCount = manager.CleanupStaleReservations(reservations);

        // Assert
        removedCount.Should().Be(1);
        reservations.Should().BeEmpty();
    }

    [Fact]
    public void CleanupStaleReservations_WithMixOfStaleAndFresh_RemovesOnlyStale()
    {
        // Arrange
        var manager = CreateManager();
        var staleId = Guid.NewGuid();
        var freshId = Guid.NewGuid();

        var reservations = new Dictionary<Guid, PendingReservations>
        {
            { staleId, new PendingReservations(staleId, 100, _clock.UtcNow) }
        };

        // Advance time to make first reservation stale
        _clock.Advance(TimeSpan.FromMinutes(11));

        // Add fresh reservation
        reservations[freshId] = new PendingReservations(freshId, 200, _clock.UtcNow);

        // Act
        var removedCount = manager.CleanupStaleReservations(reservations);

        // Assert
        removedCount.Should().Be(1);
        reservations.Should().HaveCount(1);
        reservations.Should().ContainKey(freshId);
        reservations.Should().NotContainKey(staleId);
    }

    [Fact]
    public void CleanupStaleReservations_RespectsMinimumTimeout()
    {
        // Arrange - use very short window to test minimum timeout
        var shortOptions = new TokenRateGateOptions
        {
            TokenLimit = 1000,
            WindowSeconds = 1, // Very short window
            RequestWindowSeconds = 60
        };
        var manager = new TokenTimelineManager(shortOptions, _clock, _logger);

        var reservationId = Guid.NewGuid();
        var reservations = new Dictionary<Guid, PendingReservations>
        {
            { reservationId, new PendingReservations(reservationId, 100, _clock.UtcNow) }
        };

        // Act - advance only 5 minutes (less than min timeout of 10 minutes)
        _clock.Advance(TimeSpan.FromMinutes(5));
        var removedCount = manager.CleanupStaleReservations(reservations);

        // Assert - should not be removed due to minimum timeout
        removedCount.Should().Be(0);
        reservations.Should().HaveCount(1);
    }

    [Fact]
    public void CleanupStaleReservations_RespectsMaximumTimeout()
    {
        // Arrange - use very long window to test maximum timeout
        var longOptions = new TokenRateGateOptions
        {
            TokenLimit = 1000,
            WindowSeconds = 10000, // Very long window (would give 100,000 second timeout)
            RequestWindowSeconds = 60
        };
        var manager = new TokenTimelineManager(longOptions, _clock, _logger);

        var reservationId = Guid.NewGuid();
        var reservations = new Dictionary<Guid, PendingReservations>
        {
            { reservationId, new PendingReservations(reservationId, 100, _clock.UtcNow) }
        };

        // Act - advance 25 hours (beyond max timeout of 24 hours)
        _clock.Advance(TimeSpan.FromHours(25));
        var removedCount = manager.CleanupStaleReservations(reservations);

        // Assert - should be removed due to maximum timeout
        removedCount.Should().Be(1);
        reservations.Should().BeEmpty();
    }

    [Fact]
    public void CalculateAverageEstimationEfficiency_WithNoEntries_ReturnsOne()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var efficiency = manager.CalculateAverageEstimationEfficiency();

        // Assert
        efficiency.Should().Be(1.0);
    }

    [Fact]
    public void CalculateAverageEstimationEfficiency_WithPerfectEstimation_ReturnsOne()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(actualTokens: 100, reservedTokens: 100);
        manager.AddTokenUsage(actualTokens: 200, reservedTokens: 200);

        // Act
        var efficiency = manager.CalculateAverageEstimationEfficiency();

        // Assert
        efficiency.Should().Be(1.0);
    }

    [Fact]
    public void CalculateAverageEstimationEfficiency_WithUnderestimation_ReturnsGreaterThanOne()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(actualTokens: 150, reservedTokens: 100);
        manager.AddTokenUsage(actualTokens: 250, reservedTokens: 200);

        // Act
        var efficiency = manager.CalculateAverageEstimationEfficiency();

        // Assert
        // Total: 400 actual / 300 reserved = 1.333...
        efficiency.Should().BeApproximately(1.333, 0.001);
    }

    [Fact]
    public void CalculateAverageEstimationEfficiency_WithOverestimation_ReturnsLessThanOne()
    {
        // Arrange
        var manager = CreateManager();
        manager.AddTokenUsage(actualTokens: 80, reservedTokens: 100);
        manager.AddTokenUsage(actualTokens: 160, reservedTokens: 200);

        // Act
        var efficiency = manager.CalculateAverageEstimationEfficiency();

        // Assert
        // Total: 240 actual / 300 reserved = 0.8
        efficiency.Should().BeApproximately(0.8, 0.001);
    }

    [Fact]
    public void SlidingWindow_RemovesEntriesAtCorrectTime()
    {
        // Arrange
        var manager = CreateManager();

        // Add entries at different times
        manager.AddTokenUsage(100, 150);
        _clock.Advance(TimeSpan.FromSeconds(10));

        manager.AddTokenUsage(200, 250);
        _clock.Advance(TimeSpan.FromSeconds(10));

        manager.AddTokenUsage(300, 350);

        // Act & Assert - at 50 seconds, first entry should still be valid
        _clock.Advance(TimeSpan.FromSeconds(30)); // Total 50 seconds from first entry
        var result1 = manager.CleanupExpiredEntries();
        result1.TokensFreed.Should().BeFalse();
        manager.CurrentActualUsage.Should().Be(600);

        // Act & Assert - at 61 seconds, first entry should expire
        _clock.Advance(TimeSpan.FromSeconds(11)); // Total 61 seconds from first entry
        var result2 = manager.CleanupExpiredEntries();
        result2.TokensFreed.Should().BeTrue();
        result2.RemovedTokens.Should().Be(100);
        manager.CurrentActualUsage.Should().Be(500);

        // Act & Assert - at 71 seconds, second entry should expire
        _clock.Advance(TimeSpan.FromSeconds(10)); // Total 71 seconds from first, 61 from second
        var result3 = manager.CleanupExpiredEntries();
        result3.TokensFreed.Should().BeTrue();
        result3.RemovedTokens.Should().Be(200);
        manager.CurrentActualUsage.Should().Be(300);
    }

    [Fact]
    public void CleanupStaleReservations_LogsWarning()
    {
        // Arrange
        var manager = CreateManager();
        var staleId = Guid.NewGuid();
        var reservations = new Dictionary<Guid, PendingReservations>
        {
            { staleId, new PendingReservations(staleId, 100, _clock.UtcNow) }
        };

        // Act - advance time beyond stale timeout
        _clock.Advance(TimeSpan.FromMinutes(11));
        manager.CleanupStaleReservations(reservations);

        // Assert
        _logger.LogEntries.Should().Contain(log =>
            log.LogLevel == LogLevel.Warning &&
            log.Message.Contains("Removed stale reservation"));
    }
}
