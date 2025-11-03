using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;
using TokenRateGate.Extensions.DependencyInjection;

namespace TokenRateGate.Extensions.DependencyInjection.Tests;

/// <summary>
/// Tests for configuration binding and IConfiguration integration
/// </summary>
public class ConfigurationBindingTests
{
    [Fact]
    public void AddTokenRateGate_WithConfiguration_ShouldBindAllProperties()
    {
        // Arrange
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "250000",
            ["WindowSeconds"] = "90",
            ["SafetyBufferPercentage"] = "0.05",
            ["MaxConcurrentRequests"] = "15",
            ["MaxRequestsPerMinute"] = "100",
            ["RequestWindowSeconds"] = "60",
            ["MaxWaitTime"] = "00:05:00"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGate(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.TokenLimit.Should().Be(250000);
        options.Value.WindowSeconds.Should().Be(90);
        options.Value.SafetyBufferPercentage.Should().Be(0.05);
        options.Value.MaxConcurrentRequests.Should().Be(15);
        options.Value.MaxRequestsPerMinute.Should().Be(100);
        options.Value.RequestWindowSeconds.Should().Be(60);
        options.Value.MaxWaitTime.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void AddTokenRateGate_WithConfigurationAndDefaultSection_ShouldPreferDefaultSection()
    {
        // Arrange
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "100000",
            ["Default:TokenLimit"] = "200000",
            ["Default:WindowSeconds"] = "120"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGate(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        // Default section should take precedence
        options.Value.TokenLimit.Should().Be(200000);
        options.Value.WindowSeconds.Should().Be(120);
    }

    [Fact]
    public void AddTokenRateGate_WithConfigurationNoDefaultSection_ShouldBindFromRoot()
    {
        // Arrange
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "300000",
            ["WindowSeconds"] = "180"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGate(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.TokenLimit.Should().Be(300000);
        options.Value.WindowSeconds.Should().Be(180);
    }

    [Fact]
    public void AddTokenRateGate_WithConfiguration_ShouldBindOutputEstimationStrategy()
    {
        // Arrange
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "100000",
            ["OutputEstimationStrategy"] = "FixedMultiplier",
            ["OutputMultiplier"] = "0.75"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGate(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.OutputEstimationStrategy.Should().Be(OutputEstimationStrategy.FixedMultiplier);
        options.Value.OutputMultiplier.Should().Be(0.75);
    }

    [Fact]
    public void AddNamedTokenRateGate_WithConfiguration_ShouldBindToNamedOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "500000",
            ["WindowSeconds"] = "300"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddNamedTokenRateGate("premium", configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();
        var namedOptions = optionsMonitor.Get("premium");

        namedOptions.TokenLimit.Should().Be(500000);
        namedOptions.WindowSeconds.Should().Be(300);
    }

    [Fact]
    public void ConfigurationBinding_WithJsonConfiguration_ShouldWork()
    {
        // Arrange
        var services = new ServiceCollection();

        var jsonContent = @"{
            ""TokenRateGate"": {
                ""TokenLimit"": 1000000,
                ""WindowSeconds"": 60,
                ""SafetyBufferPercentage"": 0.05,
                ""MaxConcurrentRequests"": 20
            }
        }";

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonContent)))
            .Build();

        var section = configuration.GetSection("TokenRateGate");

        // Act
        services.AddTokenRateGate(section);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.TokenLimit.Should().Be(1000000);
        options.Value.WindowSeconds.Should().Be(60);
        options.Value.SafetyBufferPercentage.Should().Be(0.05);
        options.Value.MaxConcurrentRequests.Should().Be(20);
    }

    [Fact]
    public void ConfigurationBinding_MultiTenantScenario_WithSeparateConfigurations()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var jsonContent = @"{
            ""RateGates"": {
                ""Free"": {
                    ""TokenLimit"": 10000,
                    ""WindowSeconds"": 60
                },
                ""Pro"": {
                    ""TokenLimit"": 100000,
                    ""WindowSeconds"": 60
                },
                ""Enterprise"": {
                    ""TokenLimit"": 1000000,
                    ""WindowSeconds"": 60
                }
            }
        }";

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonContent)))
            .Build();

        // Act
        services.AddNamedTokenRateGate("Free", configuration.GetSection("RateGates:Free"));
        services.AddNamedTokenRateGate("Pro", configuration.GetSection("RateGates:Pro"));
        services.AddNamedTokenRateGate("Enterprise", configuration.GetSection("RateGates:Enterprise"));

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        var freeOptions = optionsMonitor.Get("Free");
        freeOptions.TokenLimit.Should().Be(10000);

        var proOptions = optionsMonitor.Get("Pro");
        proOptions.TokenLimit.Should().Be(100000);

        var enterpriseOptions = optionsMonitor.Get("Enterprise");
        enterpriseOptions.TokenLimit.Should().Be(1000000);
    }

    [Fact]
    public void ConfigurationBinding_PartialConfiguration_ShouldUseDefaults()
    {
        // Arrange
        var services = new ServiceCollection();

        var configData = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "75000"
            // WindowSeconds not specified, should use default
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Act
        services.AddTokenRateGate(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();

        options.Value.TokenLimit.Should().Be(75000);
        // Should have default WindowSeconds (from TokenRateGateOptions defaults)
        options.Value.WindowSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TokenRateGateFactoryBuilder_WithConfiguration_ShouldBindMultipleGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var configData1 = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "50000",
            ["WindowSeconds"] = "60"
        };

        var configData2 = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "100000",
            ["WindowSeconds"] = "120"
        };

        var config1 = new ConfigurationBuilder()
            .AddInMemoryCollection(configData1)
            .Build();

        var config2 = new ConfigurationBuilder()
            .AddInMemoryCollection(configData2)
            .Build();

        // Act
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("api1", config1);
            builder.AddGate("api2", config2);
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        var api1Options = optionsMonitor.Get("api1");
        api1Options.TokenLimit.Should().Be(50000);
        api1Options.WindowSeconds.Should().Be(60);

        var api2Options = optionsMonitor.Get("api2");
        api2Options.TokenLimit.Should().Be(100000);
        api2Options.WindowSeconds.Should().Be(120);
    }

    [Fact]
    public void ConfigurationBinding_ComplexScenario_DefaultPlusNamedGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var defaultConfig = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "50000",
            ["WindowSeconds"] = "60"
        };

        var premiumConfig = new Dictionary<string, string?>
        {
            ["TokenLimit"] = "200000",
            ["WindowSeconds"] = "60"
        };

        var defaultConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(defaultConfig)
            .Build();

        var premiumConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(premiumConfig)
            .Build();

        // Act
        services.AddTokenRateGate(defaultConfiguration); // Default gate
        services.AddNamedTokenRateGate("premium", premiumConfiguration); // Named gate

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TokenRateGateOptions>>();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<TokenRateGateOptions>>();

        // Default gate
        options.Value.TokenLimit.Should().Be(50000);

        // Named gate
        var premiumOptions = optionsMonitor.Get("premium");
        premiumOptions.TokenLimit.Should().Be(200000);
    }
}
