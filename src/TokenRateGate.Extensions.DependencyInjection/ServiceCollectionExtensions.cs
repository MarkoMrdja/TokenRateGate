using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring TokenRateGate services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds TokenRateGate services to the service collection with the specified options.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Action to configure TokenRateGate options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddTokenRateGate(
        this IServiceCollection services,
        Action<TokenRateGateOptions> configureOptions)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));

        services.Configure(configureOptions);
        services.TryAddSingleton<Core.TokenRateGate>();
        services.TryAddSingleton<Abstractions.ITokenRateGate>(sp => sp.GetRequiredService<Core.TokenRateGate>());

        return services;
    }

    /// <summary>
    /// Adds TokenRateGate services to the service collection using configuration binding.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration section containing TokenRateGate options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddTokenRateGate(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        services.Configure<TokenRateGateOptions>(configuration);
        services.TryAddSingleton<Core.TokenRateGate>();
        services.TryAddSingleton<Abstractions.ITokenRateGate>(sp => sp.GetRequiredService<Core.TokenRateGate>());

        return services;
    }

    /// <summary>
    /// Adds TokenRateGate services with factory support for multi-tenant scenarios.
    /// This allows creating multiple TokenRateGate instances with different configurations.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureFactory">Action to configure the factory</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddTokenRateGateFactory(
        this IServiceCollection services,
        Action<TokenRateGateFactoryBuilder>? configureFactory = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<TokenRateGateFactory>();

        if (configureFactory != null)
        {
            var builder = new TokenRateGateFactoryBuilder(services);
            configureFactory(builder);
        }

        return services;
    }

    /// <summary>
    /// Adds a named TokenRateGate configuration for use with ITokenRateGateFactory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="name">The name for this TokenRateGate instance</param>
    /// <param name="configureOptions">Action to configure the options for this instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddNamedTokenRateGate(
        this IServiceCollection services,
        string name,
        Action<TokenRateGateOptions> configureOptions)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name cannot be null or empty", nameof(name));

        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));

        services.Configure<TokenRateGateOptions>(name, configureOptions);

        // Ensure factory is registered
        services.TryAddSingleton<TokenRateGateFactory>();

        return services;
    }

    /// <summary>
    /// Adds a named TokenRateGate configuration using configuration binding.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="name">The name for this TokenRateGate instance</param>
    /// <param name="configuration">The configuration section containing options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddNamedTokenRateGate(
        this IServiceCollection services,
        string name,
        IConfiguration configuration)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name cannot be null or empty", nameof(name));

        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        services.Configure<TokenRateGateOptions>(name, configuration);

        // Ensure factory is registered
        services.TryAddSingleton<TokenRateGateFactory>();

        return services;
    }

    /// <summary>
    /// Adds TokenRateGate health checks to monitor capacity and system health.
    /// </summary>
    /// <param name="builder">The health checks builder</param>
    /// <param name="name">The name for this health check (default: "tokenrategate")</param>
    /// <param name="tags">Optional tags for the health check</param>
    /// <returns>The health checks builder for chaining</returns>
    public static IHealthChecksBuilder AddTokenRateGate(
        this IHealthChecksBuilder builder,
        string name = "tokenrategate",
        params string[] tags)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        return builder.AddCheck<TokenRateGateHealthCheck>(name, tags: tags);
    }
}

/// <summary>
/// Builder for configuring TokenRateGate factory.
/// </summary>
public class TokenRateGateFactoryBuilder
{
    internal IServiceCollection Services { get; }

    internal TokenRateGateFactoryBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Adds a named TokenRateGate configuration.
    /// </summary>
    /// <param name="name">The name for this configuration</param>
    /// <param name="configureOptions">Action to configure the options</param>
    /// <returns>The builder for chaining</returns>
    public TokenRateGateFactoryBuilder AddGate(string name, Action<TokenRateGateOptions> configureOptions)
    {
        Services.AddNamedTokenRateGate(name, configureOptions);
        return this;
    }

    /// <summary>
    /// Adds a named TokenRateGate configuration using configuration binding.
    /// </summary>
    /// <param name="name">The name for this configuration</param>
    /// <param name="configuration">The configuration section</param>
    /// <returns>The builder for chaining</returns>
    public TokenRateGateFactoryBuilder AddGate(string name, IConfiguration configuration)
    {
        Services.AddNamedTokenRateGate(name, configuration);
        return this;
    }
}
