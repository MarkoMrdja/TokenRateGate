using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Chat;
using TokenRateGate.Abstractions;
using TokenRateGate.Core;
using TokenRateGate.OpenAI;

namespace TokenRateGate.OpenAI.Tests;

/// <summary>
/// Tests for ChatClient.WithRateLimit() extension method overloads.
/// </summary>
public class ChatClientRateLimitExtensionsTests : IDisposable
{
    private readonly ChatClient _chatClient;
    private readonly ITokenRateGate _mockRateGate;
    private readonly ITokenRateGateFactory _mockFactory;
    private readonly ILoggerFactory _mockLoggerFactory;
    private readonly IServiceProvider _mockServiceProvider;

    public ChatClientRateLimitExtensionsTests()
    {
        // Create a ChatClient for testing (requires valid API key format but won't make actual calls)
        _chatClient = new ChatClient("gpt-4", "test-api-key");

        // Create mocks
        _mockRateGate = Substitute.For<ITokenRateGate>();
        _mockFactory = Substitute.For<ITokenRateGateFactory>();
        _mockLoggerFactory = Substitute.For<ILoggerFactory>();

        // Setup mock service provider
        _mockServiceProvider = Substitute.For<IServiceProvider>();

        // Reset TokenRateGateServiceAccessor for test isolation using reflection
        ResetServiceAccessor();
    }

    public void Dispose()
    {
        // Clean up: reset the service accessor after each test using reflection
        ResetServiceAccessor();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resets the TokenRateGateServiceAccessor using reflection for proper test isolation.
    /// This is a cleaner alternative to having a public Reset() method in production code.
    /// </summary>
    private static void ResetServiceAccessor()
    {
        var field = typeof(TokenRateGateServiceAccessor).GetField("_serviceProvider",
            BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, null);
    }

    #region WithRateLimit(ChatClient, ITokenRateGate, string, ILoggerFactory?) - Direct Injection

    [Fact]
    public void WithRateLimit_DirectInjection_ValidParameters_ReturnsRateLimitedClient()
    {
        // Arrange
        const string modelName = "gpt-4";

        // Act
        var result = _chatClient.WithRateLimit(_mockRateGate, modelName, _mockLoggerFactory);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IRateLimitedChatClient>();
        result.Client.Should().BeSameAs(_chatClient);
        result.ModelName.Should().Be(modelName);
    }

    [Fact]
    public void WithRateLimit_DirectInjection_NullClient_ThrowsArgumentNullException()
    {
        // Arrange
        ChatClient? nullClient = null;

        // Act
        var act = () => nullClient!.WithRateLimit(_mockRateGate, "gpt-4", _mockLoggerFactory);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("client");
    }

    [Fact]
    public void WithRateLimit_DirectInjection_NullRateGate_ThrowsArgumentNullException()
    {
        // Arrange
        ITokenRateGate? nullRateGate = null;

        // Act
        var act = () => _chatClient.WithRateLimit(nullRateGate!, "gpt-4", _mockLoggerFactory);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("rateGate");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void WithRateLimit_DirectInjection_InvalidModelName_ThrowsArgumentException(string? invalidModelName)
    {
        // Act
        var act = () => _chatClient.WithRateLimit(_mockRateGate, invalidModelName!, _mockLoggerFactory);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Model name cannot be null or whitespace*")
            .WithParameterName("modelName");
    }

    [Fact]
    public void WithRateLimit_DirectInjection_NullLoggerFactory_DoesNotThrow()
    {
        // Act
        var act = () => _chatClient.WithRateLimit(_mockRateGate, "gpt-4", loggerFactory: null);

        // Assert
        act.Should().NotThrow("null logger factory should be accepted");
    }

    [Theory]
    [InlineData("gpt-4")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("gpt-4-turbo")]
    [InlineData("gpt-4o-mini")]
    public void WithRateLimit_DirectInjection_VariousModelNames_CreatesClient(string modelName)
    {
        // Act
        var result = _chatClient.WithRateLimit(_mockRateGate, modelName);

        // Assert
        result.Should().NotBeNull();
        result.ModelName.Should().Be(modelName);
    }

    #endregion

    #region WithRateLimit(ChatClient, string?, string) - Named Gate from DI

    [Fact]
    public void WithRateLimit_NamedGate_ValidParameters_ReturnsRateLimitedClient()
    {
        // Arrange
        const string gateName = "premium";
        const string modelName = "gpt-4";

        _mockFactory.GetOrCreate(gateName).Returns(_mockRateGate);

        _mockServiceProvider.GetService(typeof(ITokenRateGateFactory))
            .Returns(_mockFactory);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit(gateName, modelName);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IRateLimitedChatClient>();
        result.Client.Should().BeSameAs(_chatClient);
        result.ModelName.Should().Be(modelName);

        // Verify factory was called
        _mockFactory.Received(1).GetOrCreate(gateName);
    }

    [Fact]
    public void WithRateLimit_NamedGate_NullClient_ThrowsArgumentNullException()
    {
        // Arrange
        ChatClient? nullClient = null;
        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => nullClient!.WithRateLimit("premium", "gpt-4");

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("client");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithRateLimit_NamedGate_InvalidModelName_ThrowsArgumentException(string? invalidModelName)
    {
        // Arrange
        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => _chatClient.WithRateLimit("premium", invalidModelName!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Model name cannot be null or whitespace*")
            .WithParameterName("modelName");
    }

    [Fact]
    public void WithRateLimit_NamedGate_EmptyGateName_UsesDefaultGate()
    {
        // Arrange
        const string modelName = "gpt-4";

        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns(_mockRateGate);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit("", modelName);

        // Assert
        result.Should().NotBeNull();
        _mockServiceProvider.Received(1).GetService(typeof(ITokenRateGate));
    }

    [Fact]
    public void WithRateLimit_NamedGate_WhitespaceGateName_UsesDefaultGate()
    {
        // Arrange
        const string modelName = "gpt-4";

        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns(_mockRateGate);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit("   ", modelName);

        // Assert
        result.Should().NotBeNull();
        _mockServiceProvider.Received(1).GetService(typeof(ITokenRateGate));
    }

    [Fact]
    public void WithRateLimit_NamedGate_ServiceProviderNotInitialized_ThrowsInvalidOperationException()
    {
        // Arrange - Don't set service provider

        // Act
        var act = () => _chatClient.WithRateLimit("premium", "gpt-4");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Service provider has not been set*");
    }

    [Fact]
    public void WithRateLimit_NamedGate_FactoryNotRegistered_ThrowsInvalidOperationException()
    {
        // Arrange
        _mockServiceProvider.GetService(typeof(ITokenRateGateFactory))
            .Returns((ITokenRateGateFactory?)null);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => _chatClient.WithRateLimit("premium", "gpt-4");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*service*type*ITokenRateGateFactory*");
    }

    [Fact]
    public void WithRateLimit_NamedGate_NullLoggerFactory_DoesNotThrow()
    {
        // Arrange
        const string gateName = "premium";
        const string modelName = "gpt-4";

        _mockFactory.GetOrCreate(gateName).Returns(_mockRateGate);

        _mockServiceProvider.GetService(typeof(ITokenRateGateFactory))
            .Returns(_mockFactory);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns((ILoggerFactory?)null);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => _chatClient.WithRateLimit(gateName, modelName);

        // Assert
        act.Should().NotThrow("null logger factory from DI should be accepted");
    }

    [Theory]
    [InlineData("premium", "gpt-4")]
    [InlineData("basic", "gpt-3.5-turbo")]
    [InlineData("enterprise", "gpt-4o")]
    [InlineData("tenant-123", "gpt-4-turbo")]
    public void WithRateLimit_NamedGate_VariousGateNames_CallsFactoryWithCorrectName(
        string gateName,
        string modelName)
    {
        // Arrange
        _mockFactory.GetOrCreate(gateName).Returns(_mockRateGate);

        _mockServiceProvider.GetService(typeof(ITokenRateGateFactory))
            .Returns(_mockFactory);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit(gateName, modelName);

        // Assert
        result.Should().NotBeNull();
        _mockFactory.Received(1).GetOrCreate(gateName);
    }

    #endregion

    #region WithRateLimit(ChatClient, string) - Default Gate from DI

    [Fact]
    public void WithRateLimit_DefaultGate_ValidParameters_ReturnsRateLimitedClient()
    {
        // Arrange
        const string modelName = "gpt-4";

        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns(_mockRateGate);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit(modelName);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IRateLimitedChatClient>();
        result.Client.Should().BeSameAs(_chatClient);
        result.ModelName.Should().Be(modelName);
    }

    [Fact]
    public void WithRateLimit_DefaultGate_NullClient_ThrowsArgumentNullException()
    {
        // Arrange
        ChatClient? nullClient = null;
        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => nullClient!.WithRateLimit("gpt-4");

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("client");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithRateLimit_DefaultGate_InvalidModelName_ThrowsArgumentException(string? invalidModelName)
    {
        // Arrange
        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => _chatClient.WithRateLimit(invalidModelName!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Model name cannot be null or whitespace*")
            .WithParameterName("modelName");
    }

    [Fact]
    public void WithRateLimit_DefaultGate_ServiceProviderNotInitialized_ThrowsInvalidOperationException()
    {
        // Arrange - Don't set service provider

        // Act
        var act = () => _chatClient.WithRateLimit("gpt-4");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Service provider has not been set*");
    }

    [Fact]
    public void WithRateLimit_DefaultGate_RateGateNotRegistered_ThrowsInvalidOperationException()
    {
        // Arrange
        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns((ITokenRateGate?)null);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var act = () => _chatClient.WithRateLimit("gpt-4");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*service*type*ITokenRateGate*");
    }

    [Fact]
    public void WithRateLimit_DefaultGate_CallsCorrectOverload()
    {
        // Arrange
        const string modelName = "gpt-4";

        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns(_mockRateGate);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit(modelName);

        // Assert
        result.Should().NotBeNull();
        // Verify it resolved the default gate (not factory)
        _mockServiceProvider.Received(1).GetService(typeof(ITokenRateGate));
        _mockServiceProvider.DidNotReceive().GetService(typeof(ITokenRateGateFactory));
    }

    [Theory]
    [InlineData("gpt-4")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("gpt-4-turbo")]
    [InlineData("gpt-4o-mini")]
    public void WithRateLimit_DefaultGate_VariousModelNames_CreatesClient(string modelName)
    {
        // Arrange
        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns(_mockRateGate);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var result = _chatClient.WithRateLimit(modelName);

        // Assert
        result.Should().NotBeNull();
        result.ModelName.Should().Be(modelName);
    }

    #endregion

    #region Integration Tests - Verifying Correct Wrapper Behavior

    [Fact]
    public void WithRateLimit_DirectInjection_CreatesProperWrapper()
    {
        // Arrange
        const string modelName = "gpt-4";

        // Act
        var result = _chatClient.WithRateLimit(_mockRateGate, modelName);

        // Assert
        result.Should().NotBeNull();
        result.Client.Should().BeSameAs(_chatClient, "wrapper should expose underlying client");
        result.ModelName.Should().Be(modelName, "wrapper should expose model name");
    }

    [Fact]
    public void WithRateLimit_AllOverloads_ProduceSameWrapperType()
    {
        // Arrange
        const string modelName = "gpt-4";
        const string gateName = "premium";

        _mockFactory.GetOrCreate(gateName).Returns(_mockRateGate);

        _mockServiceProvider.GetService(typeof(ITokenRateGate))
            .Returns(_mockRateGate);
        _mockServiceProvider.GetService(typeof(ITokenRateGateFactory))
            .Returns(_mockFactory);
        _mockServiceProvider.GetService(typeof(ILoggerFactory))
            .Returns(_mockLoggerFactory);

        TokenRateGateServiceAccessor.SetServiceProvider(_mockServiceProvider);

        // Act
        var directResult = _chatClient.WithRateLimit(_mockRateGate, modelName);
        var namedResult = _chatClient.WithRateLimit(gateName, modelName);
        var defaultResult = _chatClient.WithRateLimit(modelName);

        // Assert
        directResult.Should().BeOfType(namedResult.GetType());
        namedResult.Should().BeOfType(defaultResult.GetType());
    }

    #endregion
}
