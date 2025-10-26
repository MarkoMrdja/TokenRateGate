using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;
using TokenRateGate.OpenAI;

namespace TokenRateGate.OpenAI.Tests;

/// <summary>
/// Tests for OpenAIChatHelper which orchestrates token estimation and usage extraction.
/// </summary>
public class OpenAIChatHelperTests
{
    [Fact]
    public void Constructor_ValidModel_SetsModelName()
    {
        // Arrange & Act
        var helper = new OpenAIChatHelper("gpt-4");

        // Assert
        helper.ModelName.Should().Be("gpt-4");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_InvalidModelName_ThrowsArgumentException(string invalidModel)
    {
        // Arrange & Act
        var act = () => new OpenAIChatHelper(invalidModel!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Model name cannot be null or whitespace*");
    }

    [Fact]
    public void Constructor_WithOptions_ConfiguresEstimator()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount,
            DefaultOutputTokens = 500
        };

        // Act
        var action = () => new OpenAIChatHelper("gpt-4", options);

        // Assert
        action.Should().NotThrow("valid options should be accepted");
    }

    [Fact]
    public void Constructor_WithLogger_ConfiguresLogging()
    {
        // Arrange
        var logger = NullLogger<OpenAIChatHelper>.Instance;

        // Act
        var action = () => new OpenAIChatHelper("gpt-4", null, logger);

        // Assert
        action.Should().NotThrow("logger should be accepted");
    }

    [Theory]
    [InlineData("gpt-4")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("gpt-4-turbo")]
    [InlineData("gpt-4o-mini")]
    public void Constructor_SupportedModels_DoesNotThrow(string model)
    {
        // Arrange & Act
        var action = () => new OpenAIChatHelper(model);

        // Assert
        action.Should().NotThrow($"{model} should be a supported OpenAI model");
    }
}
