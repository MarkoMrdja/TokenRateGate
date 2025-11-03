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
/// Tests for TokenRateGateFactory and related functionality
/// </summary>
public class TokenRateGateFactoryTests
{
    [Fact]
    public void AddTokenRateGateFactory_ShouldRegisterFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTokenRateGateFactory();

        // Assert
        var provider = services.BuildServiceProvider();
        var factory = provider.GetService<TokenRateGateFactory>();
        factory.Should().NotBeNull();

        // Verify singleton behavior
        var factory2 = provider.GetService<TokenRateGateFactory>();
        factory.Should().BeSameAs(factory2);
    }

    [Fact]
    public void AddTokenRateGateFactory_WithNullServices_ShouldThrowArgumentNullException()
    {
        // Arrange
        IServiceCollection services = null!;

        // Act & Assert
        var act = () => services.AddTokenRateGateFactory();
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddTokenRateGateFactory_WithBuilder_ShouldConfigureGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("tenant1", options =>
            {
                options.TokenLimit = 10000;
                options.WindowSeconds = 60;
            });

            builder.AddGate("tenant2", options =>
            {
                options.TokenLimit = 20000;
                options.WindowSeconds = 120;
            });
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        var gate1 = factory.GetOrCreate("tenant1");
        var gate2 = factory.GetOrCreate("tenant2");

        gate1.Should().NotBeNull();
        gate2.Should().NotBeNull();
        gate1.Should().NotBeSameAs(gate2);
    }

    [Fact]
    public void TokenRateGateFactory_GetOrCreate_ShouldReturnSameInstanceForSameName()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory();
        services.AddNamedTokenRateGate("test", options =>
        {
            options.TokenLimit = 10000;
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act
        var gate1 = factory.GetOrCreate("test");
        var gate2 = factory.GetOrCreate("test");

        // Assert
        gate1.Should().BeSameAs(gate2);
    }

    [Fact]
    public void TokenRateGateFactory_GetOrCreate_ShouldBeCaseInsensitive()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory();
        services.AddNamedTokenRateGate("TenantA", options =>
        {
            options.TokenLimit = 10000;
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act
        var gate1 = factory.GetOrCreate("TenantA");
        var gate2 = factory.GetOrCreate("tenanta");
        var gate3 = factory.GetOrCreate("TENANTA");

        // Assert
        gate1.Should().BeSameAs(gate2);
        gate2.Should().BeSameAs(gate3);
    }

    [Fact]
    public void TokenRateGateFactory_GetOrCreate_WithNullName_ShouldThrowArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act & Assert
        var act = () => factory.GetOrCreate(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("name");
    }

    [Fact]
    public void TokenRateGateFactory_GetOrCreate_WithEmptyName_ShouldThrowArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act & Assert
        var act = () => factory.GetOrCreate("");
        act.Should().Throw<ArgumentException>().WithParameterName("name");

        var act2 = () => factory.GetOrCreate("   ");
        act2.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void TokenRateGateFactory_Get_ShouldReturnExistingGateOrNull()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory();
        services.AddNamedTokenRateGate("existing", options =>
        {
            options.TokenLimit = 10000;
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act - create first
        var created = factory.GetOrCreate("existing");

        // Act - get existing
        var existing = factory.Get("existing");

        // Act - get non-existing
        var nonExisting = factory.Get("nonexisting");

        // Assert
        existing.Should().NotBeNull();
        existing.Should().BeSameAs(created);
        nonExisting.Should().BeNull();
    }

    [Fact]
    public void TokenRateGateFactory_Exists_ShouldReturnTrueForExistingGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory();
        services.AddNamedTokenRateGate("test", options =>
        {
            options.TokenLimit = 10000;
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act
        factory.GetOrCreate("test");

        // Assert
        factory.Exists("test").Should().BeTrue();
        factory.Exists("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void TokenRateGateFactory_GetAllNames_ShouldReturnAllCreatedGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("gate1", options => options.TokenLimit = 10000);
            builder.AddGate("gate2", options => options.TokenLimit = 20000);
            builder.AddGate("gate3", options => options.TokenLimit = 30000);
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Act - create gates
        factory.GetOrCreate("gate1");
        factory.GetOrCreate("gate2");
        factory.GetOrCreate("gate3");

        var names = factory.GetAllNames();

        // Assert
        names.Should().HaveCount(3);
        names.Should().Contain(new[] { "gate1", "gate2", "gate3" });
    }

    [Fact]
    public void TokenRateGateFactory_Dispose_ShouldDisposeAllGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("gate1", options => options.TokenLimit = 10000);
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();
        factory.GetOrCreate("gate1");

        // Act
        factory.Dispose();

        // Assert
        var act = () => factory.GetOrCreate("gate1");
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TokenRateGateFactoryBuilder_AddGate_WithAction_ShouldConfigureNamedOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("custom", options =>
            {
                options.TokenLimit = 50000;
                options.WindowSeconds = 30;
                options.SafetyBufferPercentage = 0.04;
            });
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();
        var namedOptions = optionsMonitor.Get("custom");

        namedOptions.TokenLimit.Should().Be(50000);
        namedOptions.WindowSeconds.Should().Be(30);
        namedOptions.SafetyBufferPercentage.Should().Be(0.04);
    }

    [Fact]
    public void TokenRateGateFactoryBuilder_AddGate_WithConfiguration_ShouldBindOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "75000",
            ["WindowSeconds"] = "90"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("configured", configuration);
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();
        var namedOptions = optionsMonitor.Get("configured");

        namedOptions.TokenLimit.Should().Be(75000);
        namedOptions.WindowSeconds.Should().Be(90);
    }

    [Fact]
    public void TokenRateGateFactoryBuilder_AddGate_ShouldAllowChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTokenRateGateFactory(builder =>
        {
            var result = builder
                .AddGate("gate1", options => options.TokenLimit = 10000)
                .AddGate("gate2", options => options.TokenLimit = 20000)
                .AddGate("gate3", options => options.TokenLimit = 30000);

            // Assert chaining
            result.Should().BeSameAs(builder);
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        factory.GetOrCreate("gate1").Should().NotBeNull();
        factory.GetOrCreate("gate2").Should().NotBeNull();
        factory.GetOrCreate("gate3").Should().NotBeNull();
    }
}
