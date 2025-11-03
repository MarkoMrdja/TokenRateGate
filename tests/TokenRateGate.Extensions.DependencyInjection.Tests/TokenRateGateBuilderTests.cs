using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TokenRateGate.Extensions.DependencyInjection;

namespace TokenRateGate.Extensions.DependencyInjection.Tests;

/// <summary>
/// Tests for TokenRateGateBuilder functionality (tested through public ITokenRateGateBuilder interface)
/// </summary>
public class TokenRateGateBuilderTests
{
    [Fact]
    public void Builder_ShouldImplementITokenRateGateBuilder()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 10000;
        });

        // Assert
        builder.Should().NotBeNull();
        builder.Should().BeAssignableTo<ITokenRateGateBuilder>();
    }

    [Fact]
    public void Builder_Services_ShouldBeAccessible()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 10000;
        });

        // Assert
        builder.Services.Should().NotBeNull();
        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void Builder_AllowsAccessToServicesForExtensions()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 10000;
        });

        // Extensions can add additional services through the Services property
        builder.Services.AddSingleton<string>("test-extension");

        // Assert
        var provider = services.BuildServiceProvider();
        var extension = provider.GetService<string>();
        extension.Should().Be("test-extension");
    }

    [Fact]
    public void Builder_IntegrationWithAddTokenRateGate_ShouldProvideFluentAPI()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 50000;
        });

        // Simulate extension method chaining
        builder.Services.AddSingleton<string>("custom");

        // Assert
        builder.Should().NotBeNull();
        builder.Services.Should().BeSameAs(services);

        var provider = services.BuildServiceProvider();
        provider.GetService<string>().Should().Be("custom");
    }

    [Fact]
    public void Builder_AllowsExtensionMethodPattern()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act - This demonstrates how extension methods can be built on top of ITokenRateGateBuilder
        var builder = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 100000;
        });

        // Simulated extension method that returns the builder
        var result = AddCustomFeature(builder);

        // Assert
        result.Should().BeSameAs(builder);
        var provider = services.BuildServiceProvider();
        provider.GetService<CustomFeature>().Should().NotBeNull();
        provider.GetService<CustomFeature>()!.Value.Should().Be(42);
    }

    // Helper method to simulate extension method pattern
    private static ITokenRateGateBuilder AddCustomFeature(ITokenRateGateBuilder builder)
    {
        builder.Services.AddSingleton<CustomFeature>(new CustomFeature { Value = 42 });
        return builder;
    }

    // Helper class for testing extension method pattern
    private class CustomFeature
    {
        public int Value { get; set; }
    }
}
