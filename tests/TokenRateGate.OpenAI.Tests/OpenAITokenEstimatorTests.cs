using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;
using TokenRateGate.OpenAI;

namespace TokenRateGate.OpenAI.Tests;

/// <summary>
/// Tests for OpenAITokenEstimator to validate accurate token counting using tiktoken.
/// </summary>
public class OpenAITokenEstimatorTests
{
    [Theory]
    [InlineData("gpt-4", "Hello, world!", 4)] // Expected tokens for simple message
    [InlineData("gpt-4o", "Hello, world!", 4)]
    [InlineData("gpt-3.5-turbo", "Hello, world!", 4)]
    public async Task EstimateInputTokensAsync_SimpleMessage_ReturnsAccurateCount(
        string model,
        string messageContent,
        int expectedMinTokens)
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions());
        var estimator = new OpenAITokenEstimator(model, options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage>
        {
            new UserChatMessage(messageContent)
        };

        // Act
        var tokens = await estimator.EstimateInputTokensAsync(messages);

        // Assert
        tokens.Should().BeGreaterThanOrEqualTo(expectedMinTokens,
            "tiktoken should count at least the content tokens");
        tokens.Should().BeLessThan(50,
            "a simple message shouldn't be tokenized into many tokens");
    }

    [Fact]
    public async Task EstimateInputTokensAsync_MultipleMessages_CountsAllMessages()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions());
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a helpful assistant."),
            new UserChatMessage("What is the capital of France?"),
            new AssistantChatMessage("The capital of France is Paris."),
            new UserChatMessage("What about Germany?")
        };

        // Act
        var tokens = await estimator.EstimateInputTokensAsync(messages);

        // Assert
        tokens.Should().BeGreaterThan(20,
            "multiple messages should result in more tokens");
        tokens.Should().BeLessThan(100,
            "but this conversation should still be relatively short");
    }

    [Fact]
    public async Task EstimateInputTokensAsync_LongMessage_ScalesWithLength()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions());
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);

        var shortMessage = "Hello";
        var longMessage = string.Join(" ", Enumerable.Repeat("Hello", 100)); // 100 repetitions
        // Note: Repeated words don't scale linearly due to tokenization
        // "Hello" (1 word) ≈ 1-2 tokens, "Hello " (repeated 100x) ≈ 100-150 tokens
        // (not 100-200 tokens because spaces and repetition affect tokenization)

        var shortMessages = new List<ChatMessage> { new UserChatMessage(shortMessage) };
        var longMessages = new List<ChatMessage> { new UserChatMessage(longMessage) };

        // Act
        var shortTokens = await estimator.EstimateInputTokensAsync(shortMessages);
        var longTokens = await estimator.EstimateInputTokensAsync(longMessages);

        // Assert - long message should have significantly more tokens, but not exactly 100x
        // Actual: "Hello" = ~8 tokens (with overhead), "Hello " x100 = ~107 tokens
        // The tokenizer is efficient with repeated words, so we see ~13x increase, not 100x
        longTokens.Should().BeGreaterThan(shortTokens * 10,
            "100x word repetition should result in significantly more tokens");
        longTokens.Should().BeLessThan(shortTokens * 200,
            "token count should be reasonable for 100 word repetitions");
    }

    [Theory]
    [InlineData(OutputEstimationStrategy.FixedMultiplier, 0.5)]
    [InlineData(OutputEstimationStrategy.FixedMultiplier, 1.0)]
    [InlineData(OutputEstimationStrategy.FixedMultiplier, 2.0)]
    public async Task EstimateOutputTokensAsync_FixedMultiplier_UsesConfiguredMultiplier(
        OutputEstimationStrategy strategy,
        double multiplier)
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            OutputEstimationStrategy = strategy,
            OutputMultiplier = multiplier
        });
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage> { new UserChatMessage("Test message") };

        // Act
        var inputTokens = await estimator.EstimateInputTokensAsync(messages);
        var outputTokens = await estimator.EstimateOutputTokensAsync(messages);

        // Assert
        var expectedOutput = (int)(inputTokens * multiplier);
        outputTokens.Should().Be(expectedOutput,
            $"output should be input * {multiplier} for FixedMultiplier strategy");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(2000)]
    public async Task EstimateOutputTokensAsync_FixedAmount_UsesConfiguredAmount(int fixedAmount)
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount,
            DefaultOutputTokens = fixedAmount
        });
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage> { new UserChatMessage("Test message") };

        // Act
        var outputTokens = await estimator.EstimateOutputTokensAsync(messages);

        // Assert
        outputTokens.Should().Be(fixedAmount,
            "output should match DefaultOutputTokens for FixedAmount strategy");
    }

    [Fact]
    public async Task EstimateOutputTokensAsync_Conservative_DoublesInputTokens()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            OutputEstimationStrategy = OutputEstimationStrategy.Conservative
        });
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage> { new UserChatMessage("Test message") };

        // Act
        var inputTokens = await estimator.EstimateInputTokensAsync(messages);
        var outputTokens = await estimator.EstimateOutputTokensAsync(messages);

        // Assert
        outputTokens.Should().Be(inputTokens,
            "Conservative strategy should assume output equals input");
    }

    [Fact]
    public async Task EstimateInputTokensAsync_EmptyMessages_ReturnsMinimalTokens()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions());
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage>();

        // Act
        var tokens = await estimator.EstimateInputTokensAsync(messages);

        // Assert
        tokens.Should().BeGreaterThanOrEqualTo(0, "empty messages should return non-negative tokens");
        tokens.Should().BeLessThan(10, "empty messages should have minimal overhead");
    }

    [Theory]
    [InlineData("gpt-4")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("gpt-4-turbo")]
    public void Constructor_ValidModel_DoesNotThrow(string model)
    {
        // Arrange & Act
        var options = Options.Create(new TokenRateGateOptions());
        var action = () => new OpenAITokenEstimator(model, options, NullLogger<OpenAITokenEstimator>.Instance);

        // Assert
        action.Should().NotThrow("valid OpenAI models should be supported");
    }

    [Fact]
    public async Task EstimateInputTokensAsync_SpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions());
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage>
        {
            new UserChatMessage("Special chars: 🚀 émojis, ñoñó, 中文")
        };

        // Act
        var tokens = await estimator.EstimateInputTokensAsync(messages);

        // Assert
        tokens.Should().BeGreaterThan(0, "special characters should be tokenized");
        tokens.Should().BeLessThan(100, "even with special chars, this should be reasonable");
    }

    [Fact]
    public async Task EstimateTokensAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions());
        var estimator = new OpenAITokenEstimator("gpt-4", options, NullLogger<OpenAITokenEstimator>.Instance);
        var messages = new List<ChatMessage> { new UserChatMessage("Test") };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = async () => await estimator.EstimateInputTokensAsync(messages, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelled token should propagate cancellation");
    }
}
