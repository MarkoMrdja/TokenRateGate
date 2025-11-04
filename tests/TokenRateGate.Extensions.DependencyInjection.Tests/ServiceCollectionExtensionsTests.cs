using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Options;
using TokenRateGate.Extensions.DependencyInjection;

namespace TokenRateGate.Extensions.DependencyInjection.Tests;

/// <summary>
/// Tests for ServiceCollectionExtensions methods
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTokenRateGate_WithAction_ShouldRegisterServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var configured = false;

        // Act
        var result = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 50000;
            options.WindowSeconds = 60;
            configured = true;
        });

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(services);

        var provider = services.BuildServiceProvider();

        // Verify singleton registration
        var gate1 = provider.GetService<Core.TokenRateGate>();
        var gate2 = provider.GetService<Core.TokenRateGate>();
        gate1.Should().NotBeNull();
        gate1.Should().BeSameAs(gate2);

        // Verify interface registration
        var iGate = provider.GetService<ITokenRateGate>();
        iGate.Should().NotBeNull();
        iGate.Should().BeSameAs(gate1);

        // Verify options were configured
        configured.Should().BeTrue();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();
        options.Value.TokenLimit.Should().Be(50000);
        options.Value.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void AddTokenRateGate_WithNullServices_ShouldThrowArgumentNullException()
    {
        // Arrange
        IServiceCollection services = null!;

        // Act & Assert
        var act = () => services.AddTokenRateGate(options => { });
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddTokenRateGate_WithNullConfigureOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddTokenRateGate((Action<TokenRateGateOptions>)null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configureOptions");
    }

    [Fact]
    public void AddTokenRateGate_WithoutAction_ShouldRegisterServicesWithDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        var builder = services.AddTokenRateGate();

        // Assert
        builder.Should().NotBeNull();

        var provider = services.BuildServiceProvider();
        var gate = provider.GetService<Core.TokenRateGate>();
        gate.Should().NotBeNull();

        var iGate = provider.GetService<ITokenRateGate>();
        iGate.Should().NotBeNull();
        iGate.Should().BeSameAs(gate);
    }

    [Fact]
    public void AddTokenRateGate_WithConfiguration_ShouldBindOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "100000",
            ["WindowSeconds"] = "120",
            ["SafetyBufferPercentage"] = "0.1",
            ["MaxConcurrentRequests"] = "10"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        var builder = services.AddTokenRateGate(configuration);

        // Assert
        builder.Should().NotBeNull();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.TokenLimit.Should().Be(100000);
        options.Value.WindowSeconds.Should().Be(120);
        options.Value.SafetyBufferPercentage.Should().Be(0.1);
        options.Value.MaxConcurrentRequests.Should().Be(10);
    }

    [Fact]
    public void AddTokenRateGate_WithConfigurationDefaultSection_ShouldBindFromDefaultSection()
    {
        // Arrange
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["Default:TokenLimit"] = "200000",
            ["Default:WindowSeconds"] = "60"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGate(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.TokenLimit.Should().Be(200000);
        options.Value.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void AddTokenRateGate_WithNullConfiguration_ShouldThrowArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddTokenRateGate((IConfiguration)null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configuration");
    }

    [Fact]
    public void AddTokenRateGate_CalledMultipleTimes_ShouldNotRegisterDuplicates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTokenRateGate(options => options.TokenLimit = 10000);
        services.AddTokenRateGate(options => options.TokenLimit = 20000);

        // Assert - TryAddSingleton should prevent duplicates
        var provider = services.BuildServiceProvider();
        var gate1 = provider.GetService<Core.TokenRateGate>();
        var gate2 = provider.GetService<Core.TokenRateGate>();

        gate1.Should().BeSameAs(gate2);

        // Both Configure calls are applied, last one wins for options
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();
        options.Value.TokenLimit.Should().Be(20000);
    }

    [Fact]
    public void AddTokenRateGate_ReturnsServiceCollection_AllowsChaining()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddTokenRateGate(options =>
        {
            options.TokenLimit = 50000;
        });

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(services);
    }
}
