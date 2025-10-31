using Microsoft.Extensions.DependencyInjection;

namespace TokenRateGate.Extensions.DependencyInjection;

/// <summary>
/// Builder for configuring TokenRateGate services with fluent API.
/// </summary>
internal class TokenRateGateBuilder : ITokenRateGateBuilder
{
    /// <summary>
    /// Gets the service collection being configured.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Creates a new TokenRateGate builder.
    /// </summary>
    /// <param name="services">The service collection to configure</param>
    public TokenRateGateBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }
}
