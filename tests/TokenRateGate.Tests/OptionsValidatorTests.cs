using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Validation;

namespace TokenRateGate.Tests;

public class OptionsValidatorTests
{
    private static TokenRateGateOptions CreateValidOptions() => new()
    {
        TokenLimit = 1000,
        WindowSeconds = 60,
        SafetyBufferPercentage = 0.1,
        MaxConcurrentRequests = 10,
        MaxRequestsPerMinute = 100,
        RequestWindowSeconds = 60,
        OutputMultiplier = 0.5,
        DefaultOutputTokens = 100
    };

    [Fact]
    public void Validate_WithValidOptions_DoesNotThrow()
    {
        // Arrange
        var options = CreateValidOptions();

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        TokenRateGateOptions? options = null;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Validate_WithInvalidTokenLimit_Throws(int tokenLimit)
    {
        // Arrange
        var options = CreateValidOptions();
        options.TokenLimit = tokenLimit;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*TokenLimit must be positive*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    public void Validate_WithInvalidWindowSeconds_Throws(int windowSeconds)
    {
        // Arrange
        var options = CreateValidOptions();
        options.WindowSeconds = windowSeconds;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*WindowSeconds must be positive*");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-1.0)]
    public void Validate_WithNegativeSafetyBuffer_Throws(double percentage)
    {
        // Arrange
        var options = CreateValidOptions();
        options.SafetyBufferPercentage = percentage;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*SafetyBufferPercentage cannot be negative*");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Validate_WithSafetyBufferAtOrAbove100Percent_Throws(double percentage)
    {
        // Arrange
        var options = CreateValidOptions();
        options.SafetyBufferPercentage = percentage;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*SafetyBufferPercentage must be less than 1.0*");
    }

    [Theory]
    [InlineData(0.51)]
    [InlineData(0.6)]
    [InlineData(0.9)]
    public void Validate_WithHighSafetyBuffer_LogsWarning(double percentage)
    {
        // Arrange
        var options = CreateValidOptions();
        options.SafetyBufferPercentage = percentage;
        var logger = Substitute.For<ILogger>();

        // Act
        TokenRateGateOptionsValidator.Validate(options, logger);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("SafetyBufferPercentage")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.3)]
    [InlineData(0.1)]
    public void Validate_WithReasonableSafetyBuffer_DoesNotLogWarning(double percentage)
    {
        // Arrange
        var options = CreateValidOptions();
        options.SafetyBufferPercentage = percentage;
        var logger = Substitute.For<ILogger>();

        // Act
        TokenRateGateOptionsValidator.Validate(options, logger);

        // Assert
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Validate_WithInvalidMaxConcurrentRequests_Throws(int maxConcurrentRequests)
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxConcurrentRequests = maxConcurrentRequests;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxConcurrentRequests must be positive*");
    }

    [Theory]
    [InlineData(10_001)]
    [InlineData(20_000)]
    [InlineData(100_000)]
    public void Validate_WithExcessiveMaxConcurrentRequests_Throws(int maxConcurrentRequests)
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxConcurrentRequests = maxConcurrentRequests;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*exceeds safe limit (10,000)*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidMaxRequestsPerMinute_Throws(int maxRequestsPerMinute)
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxRequestsPerMinute = maxRequestsPerMinute;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxRequestsPerMinute must be positive*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    public void Validate_WithInvalidRequestWindowSeconds_Throws(int requestWindowSeconds)
    {
        // Arrange
        var options = CreateValidOptions();
        options.RequestWindowSeconds = requestWindowSeconds;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*RequestWindowSeconds must be positive*");
    }

    [Fact]
    public void Validate_WithNullMaxWaitTime_DoesNotThrow()
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxWaitTime = null;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithZeroMaxWaitTime_Throws()
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxWaitTime = TimeSpan.Zero;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxWaitTime must be positive*");
    }

    [Fact]
    public void Validate_WithNegativeMaxWaitTime_Throws()
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxWaitTime = TimeSpan.FromSeconds(-10);

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxWaitTime must be positive*");
    }

    [Fact]
    public void Validate_WithExcessiveMaxWaitTime_Throws()
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxWaitTime = TimeSpan.FromHours(25);

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxWaitTime cannot exceed 24 hours*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(1440)] // 24 hours in minutes
    public void Validate_WithValidMaxWaitTime_DoesNotThrow(int minutes)
    {
        // Arrange
        var options = CreateValidOptions();
        options.MaxWaitTime = TimeSpan.FromMinutes(minutes);

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-1.0)]
    [InlineData(-5.0)]
    public void Validate_WithNegativeOutputMultiplier_Throws(double multiplier)
    {
        // Arrange
        var options = CreateValidOptions();
        options.OutputMultiplier = multiplier;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*OutputMultiplier cannot be negative*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(-1000)]
    public void Validate_WithNegativeDefaultOutputTokens_Throws(int defaultOutputTokens)
    {
        // Arrange
        var options = CreateValidOptions();
        options.DefaultOutputTokens = defaultOutputTokens;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*DefaultOutputTokens cannot be negative*");
    }

    [Fact]
    public void Validate_WithDefaultOutputTokensExceedingTokenLimit_Throws()
    {
        // Arrange
        var options = CreateValidOptions();
        options.TokenLimit = 1000;
        options.DefaultOutputTokens = 1001;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*DefaultOutputTokens*cannot exceed TokenLimit*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(1000)]
    public void Validate_WithValidDefaultOutputTokens_DoesNotThrow(int defaultOutputTokens)
    {
        // Arrange
        var options = CreateValidOptions();
        options.TokenLimit = 1000;
        options.DefaultOutputTokens = defaultOutputTokens;

        // Act
        var act = () => TokenRateGateOptionsValidator.Validate(options);

        // Assert
        act.Should().NotThrow();
    }
}
