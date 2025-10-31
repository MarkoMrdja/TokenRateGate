using Microsoft.Extensions.DependencyInjection;

namespace TokenRateGate.Extensions.DependencyInjection;

/// <summary>
/// Builder interface for configuring TokenRateGate services with fluent API.
/// Enables extension methods for adding OpenAI, Azure, and other integrations.
/// </summary>
public interface ITokenRateGateBuilder
{
    /// <summary>
    /// Gets the service collection being configured.
    /// </summary>
    IServiceCollection Services { get; }
}
