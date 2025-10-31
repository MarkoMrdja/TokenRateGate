using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core.Options;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Extension methods for registering OpenAI integration components in dependency injection.
/// Provides convenient methods to add OpenAIChatHelper to the service collection.
/// </summary>
public static class OpenAIServiceCollectionExtensions
{
    /// <summary>
    /// Registers an OpenAI chat helper for the specified model in the dependency injection container.
    /// The helper will be registered as a singleton and can be injected into services.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo", "gpt-4o")</param>
    /// <returns>The service collection for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when services is null</exception>
    /// <exception cref="ArgumentException">Thrown when modelName is null or whitespace</exception>
    /// <remarks>
    /// This method registers OpenAIChatHelper as a singleton service. The helper will be created
    /// with the specified model name and will attempt to resolve IOptions&lt;TokenRateGateOptions&gt;
    /// and ILogger&lt;OpenAIChatHelper&gt; from the service provider if available.
    ///
    /// Example usage:
    /// <code>
    /// services.AddTokenRateGate(options => {
    ///     options.TokenLimit = 1000000;
    ///     options.WindowSeconds = 60;
    /// });
    /// services.AddOpenAIChatHelper("gpt-4");
    ///
    /// // In a service:
    /// public class ChatService
    /// {
    ///     private readonly OpenAIChatHelper _helper;
    ///
    ///     public ChatService(OpenAIChatHelper helper)
    ///     {
    ///         _helper = helper;
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public static IServiceCollection AddOpenAIChatHelper(
        this IServiceCollection services,
        string modelName)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        services.TryAddSingleton<OpenAIChatHelper>(serviceProvider =>
        {
            // Try to resolve TokenRateGateOptions from DI (may not be registered)
            var options = serviceProvider.GetService<IOptions<TokenRateGateOptions>>()?.Value;

            // Try to resolve logger from DI (may not be registered)
            var logger = serviceProvider.GetService<ILogger<OpenAIChatHelper>>();

            return new OpenAIChatHelper(modelName, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers a named OpenAI chat helper for the specified model in the dependency injection container.
    /// This allows registering multiple helpers for different models.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="name">The name to register the helper under (used as a keyed service)</param>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo", "gpt-4o")</param>
    /// <returns>The service collection for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when services is null</exception>
    /// <exception cref="ArgumentException">Thrown when name or modelName is null or whitespace</exception>
    /// <remarks>
    /// This method is useful when you need to work with multiple OpenAI models in the same application.
    /// Each helper is registered with a unique name and can be retrieved using keyed service resolution.
    ///
    /// Example usage:
    /// <code>
    /// services.AddTokenRateGate(options => { ... });
    /// services.AddNamedOpenAIChatHelper("gpt4", "gpt-4");
    /// services.AddNamedOpenAIChatHelper("gpt35", "gpt-3.5-turbo");
    ///
    /// // In a service (requires .NET 8+ for keyed services):
    /// public class ChatService
    /// {
    ///     private readonly OpenAIChatHelper _gpt4Helper;
    ///     private readonly OpenAIChatHelper _gpt35Helper;
    ///
    ///     public ChatService(
    ///         [FromKeyedServices("gpt4")] OpenAIChatHelper gpt4Helper,
    ///         [FromKeyedServices("gpt35")] OpenAIChatHelper gpt35Helper)
    ///     {
    ///         _gpt4Helper = gpt4Helper;
    ///         _gpt35Helper = gpt35Helper;
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public static IServiceCollection AddNamedOpenAIChatHelper(
        this IServiceCollection services,
        string name,
        string modelName)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be null or whitespace", nameof(name));

        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        // Register as a keyed singleton (requires .NET 8+)
        services.TryAddKeyedSingleton<OpenAIChatHelper>(name, (serviceProvider, key) =>
        {
            // Try to resolve TokenRateGateOptions from DI (may not be registered)
            var options = serviceProvider.GetService<IOptions<TokenRateGateOptions>>()?.Value;

            // Try to resolve logger from DI (may not be registered)
            var logger = serviceProvider.GetService<ILogger<OpenAIChatHelper>>();

            return new OpenAIChatHelper(modelName, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers an OpenAI chat helper factory in the dependency injection container.
    /// The factory allows creating helpers for different models on demand.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when services is null</exception>
    /// <remarks>
    /// This method registers IOpenAIChatHelperFactory as a singleton service.
    /// The factory can be used to create OpenAIChatHelper instances for different models at runtime.
    ///
    /// Example usage:
    /// <code>
    /// services.AddTokenRateGate(options => { ... });
    /// services.AddOpenAIChatHelperFactory();
    ///
    /// // In a service:
    /// public class ChatService
    /// {
    ///     private readonly IOpenAIChatHelperFactory _helperFactory;
    ///
    ///     public ChatService(IOpenAIChatHelperFactory helperFactory)
    ///     {
    ///         _helperFactory = helperFactory;
    ///     }
    ///
    ///     public async Task ProcessWithModel(string modelName, string message)
    ///     {
    ///         var helper = _helperFactory.Create(modelName);
    ///         // Use helper...
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public static IServiceCollection AddOpenAIChatHelperFactory(
        this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<IOpenAIChatHelperFactory, OpenAIChatHelperFactory>();

        return services;
    }
}

/// <summary>
/// Factory interface for creating OpenAIChatHelper instances for different models.
/// This is useful in scenarios where the model name is determined at runtime.
/// </summary>
internal interface IOpenAIChatHelperFactory
{
    /// <summary>
    /// Creates an OpenAI chat helper for the specified model.
    /// </summary>
    /// <param name="modelName">The OpenAI model name</param>
    /// <returns>A new OpenAIChatHelper instance configured for the specified model</returns>
    OpenAIChatHelper Create(string modelName);
}

/// <summary>
/// Default implementation of IOpenAIChatHelperFactory.
/// </summary>
internal class OpenAIChatHelperFactory : IOpenAIChatHelperFactory
{
    private readonly IServiceProvider _serviceProvider;

    public OpenAIChatHelperFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public OpenAIChatHelper Create(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));

        // Try to resolve TokenRateGateOptions from DI (may not be registered)
        var options = _serviceProvider.GetService<IOptions<TokenRateGateOptions>>()?.Value;

        // Try to resolve logger from DI (may not be registered)
        var logger = _serviceProvider.GetService<ILogger<OpenAIChatHelper>>();

        return new OpenAIChatHelper(modelName, options, logger);
    }
}
