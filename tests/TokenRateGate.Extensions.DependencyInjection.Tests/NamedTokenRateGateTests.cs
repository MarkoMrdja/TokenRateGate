using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core.Options;
using TokenRateGate.Extensions.DependencyInjection;

namespace TokenRateGate.Extensions.DependencyInjection.Tests;

/// <summary>
/// Tests for AddNamedTokenRateGate functionality
/// </summary>
public class NamedTokenRateGateTests
{
    [Fact]
    public void AddNamedTokenRateGate_WithAction_ShouldRegisterNamedOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNamedTokenRateGate("tenant1", options =>
        {
            options.TokenLimit = 100000;
            options.WindowSeconds = 60;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        var tenant1Options = optionsMonitor.Get("tenant1");
        tenant1Options.TokenLimit.Should().Be(100000);
        tenant1Options.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void AddNamedTokenRateGate_WithNullServices_ShouldThrowArgumentNullException()
    {
        // Arrange
        IServiceCollection services = null!;

        // Act & Assert
        var act = () => services.AddNamedTokenRateGate("test", options => { });
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddNamedTokenRateGate_WithNullOrEmptyName_ShouldThrowArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act1 = () => services.AddNamedTokenRateGate(null!, options => { });
        act1.Should().Throw<ArgumentException>().WithParameterName("name");

        var act2 = () => services.AddNamedTokenRateGate("", options => { });
        act2.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void AddNamedTokenRateGate_WithNullConfigureOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddNamedTokenRateGate("test", (Action<TokenRateGateOptions>)null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configureOptions");
    }

    [Fact]
    public void AddNamedTokenRateGate_ShouldAutomaticallyRegisterFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNamedTokenRateGate("test", options =>
        {
            options.TokenLimit = 10000;
        });

        // Assert - Factory should be registered
        var provider = services.BuildServiceProvider();
        var factory = provider.GetService<TokenRateGateFactory>();
        factory.Should().NotBeNull();
    }

    [Fact]
    public void AddNamedTokenRateGate_MultipleTenants_ShouldRegisterSeparateConfigurations()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNamedTokenRateGate("tenant1", options =>
        {
            options.TokenLimit = 50000;
            options.WindowSeconds = 60;
        });

        services.AddNamedTokenRateGate("tenant2", options =>
        {
            options.TokenLimit = 100000;
            options.WindowSeconds = 120;
        });

        services.AddNamedTokenRateGate("tenant3", options =>
        {
            options.TokenLimit = 25000;
            options.WindowSeconds = 30;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        var tenant1 = optionsMonitor.Get("tenant1");
        tenant1.TokenLimit.Should().Be(50000);
        tenant1.WindowSeconds.Should().Be(60);

        var tenant2 = optionsMonitor.Get("tenant2");
        tenant2.TokenLimit.Should().Be(100000);
        tenant2.WindowSeconds.Should().Be(120);

        var tenant3 = optionsMonitor.Get("tenant3");
        tenant3.TokenLimit.Should().Be(25000);
        tenant3.WindowSeconds.Should().Be(30);
    }

    [Fact]
    public void AddNamedTokenRateGate_WithConfiguration_ShouldBindOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "150000",
            ["WindowSeconds"] = "90",
            ["SafetyBufferPercentage"] = "0.05"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddNamedTokenRateGate("configured", configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        var options = optionsMonitor.Get("configured");
        options.TokenLimit.Should().Be(150000);
        options.WindowSeconds.Should().Be(90);
        options.SafetyBufferPercentage.Should().Be(0.05);
    }

    [Fact]
    public void AddNamedTokenRateGate_WithConfiguration_NullConfiguration_ShouldThrowArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddNamedTokenRateGate("test", (IConfiguration)null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configuration");
    }

    [Fact]
    public void AddNamedTokenRateGate_WithConfiguration_NullOrEmptyName_ShouldThrowArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        // Act & Assert
        var act1 = () => services.AddNamedTokenRateGate(null!, configuration);
        act1.Should().Throw<ArgumentException>().WithParameterName("name");

        var act2 = () => services.AddNamedTokenRateGate("", configuration);
        act2.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void AddNamedTokenRateGate_IntegrationWithFactory_ShouldWorkCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNamedTokenRateGate("api1", options =>
        {
            options.TokenLimit = 10000;
            options.WindowSeconds = 60;
        });

        services.AddNamedTokenRateGate("api2", options =>
        {
            options.TokenLimit = 50000;
            options.WindowSeconds = 120;
        });

        // Act
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        var gate1 = factory.GetOrCreate("api1");
        var gate2 = factory.GetOrCreate("api2");

        // Assert
        gate1.Should().NotBeNull();
        gate2.Should().NotBeNull();
        gate1.Should().NotBeSameAs(gate2);

        // Verify same instances are returned
        var gate1Again = factory.GetOrCreate("api1");
        gate1Again.Should().BeSameAs(gate1);
    }

    [Fact]
    public void AddNamedTokenRateGate_AllowsChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        var result = services
            .AddNamedTokenRateGate("gate1", options => options.TokenLimit = 10000)
            .AddNamedTokenRateGate("gate2", options => options.TokenLimit = 20000)
            .AddNamedTokenRateGate("gate3", options => options.TokenLimit = 30000);

        // Assert
        result.Should().BeSameAs(services);

        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        optionsMonitor.Get("gate1").TokenLimit.Should().Be(10000);
        optionsMonitor.Get("gate2").TokenLimit.Should().Be(20000);
        optionsMonitor.Get("gate3").TokenLimit.Should().Be(30000);
    }
}
