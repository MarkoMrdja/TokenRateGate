using Microsoft.Extensions.DependencyInjection;

namespace TokenRateGate.Core;

/// <summary>
/// Provides static access to TokenRateGate services for use in extension methods.
/// This enables ChatClient extension methods to access DI-registered services
/// without requiring manual service provider passing.
/// </summary>
/// <remarks>
/// This is an advanced pattern that allows extension methods on third-party types
/// (like ChatClient) to access dependency injection services. The service provider
/// is stored during application startup and accessed later by extension methods.
///
/// Thread-safety: This class uses thread-safe operations for setting and getting
/// the service provider.
/// </remarks>
public static class TokenRateGateServiceAccessor
{
    private static IServiceProvider? _serviceProvider;
    private static readonly object _lock = new();

    /// <summary>
    /// Sets the service provider for use by TokenRateGate extension methods.
    /// This should be called once during application startup, typically from
    /// a startup hook or application initialization code.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider</param>
    /// <exception cref="ArgumentNullException">Thrown when serviceProvider is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when attempting to set the service provider more than once</exception>
    public static void SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        lock (_lock)
        {
            if (_serviceProvider != null)
            {
                throw new InvalidOperationException(
                    "Service provider has already been set. TokenRateGateServiceAccessor.SetServiceProvider " +
                    "should only be called once during application startup.");
            }

            _serviceProvider = serviceProvider;
        }
    }

    /// <summary>
    /// Gets the current service provider.
    /// </summary>
    /// <returns>The application's service provider</returns>
    /// <exception cref="InvalidOperationException">Thrown when the service provider has not been set</exception>
    public static IServiceProvider ServiceProvider
    {
        get
        {
            var provider = _serviceProvider;
            if (provider == null)
            {
                throw new InvalidOperationException(
                    "Service provider has not been set. Call TokenRateGateServiceAccessor.SetServiceProvider " +
                    "during application startup before using ChatClient extension methods.");
            }
            return provider;
        }
    }

    /// <summary>
    /// Gets a required service from the service provider.
    /// </summary>
    /// <typeparam name="T">The service type to retrieve</typeparam>
    /// <returns>The requested service</returns>
    /// <exception cref="InvalidOperationException">Thrown when the service provider has not been set or the service is not registered</exception>
    public static T GetRequiredService<T>() where T : notnull
    {
        return ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Gets a service from the service provider, or null if not registered.
    /// </summary>
    /// <typeparam name="T">The service type to retrieve</typeparam>
    /// <returns>The requested service, or null if not registered</returns>
    /// <exception cref="InvalidOperationException">Thrown when the service provider has not been set</exception>
    public static T? GetService<T>()
    {
        return ServiceProvider.GetService<T>();
    }
}
