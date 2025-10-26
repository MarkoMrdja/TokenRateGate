using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Extensions.DependencyInjection;

/// <summary>
/// Factory for creating and managing multiple TokenRateGate instances.
/// Enables multi-tenant scenarios where different models or endpoints have different rate limits.
/// </summary>
public class TokenRateGateFactory : ITokenRateGateFactory, IDisposable
{
    private readonly IOptionsMonitor<TokenRateGateOptions> _optionsMonitor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, Core.TokenRateGate> _gates;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the TokenRateGateFactory.
    /// </summary>
    /// <param name="optionsMonitor">Options monitor for accessing named configurations</param>
    /// <param name="loggerFactory">Logger factory for creating loggers for each gate</param>
    public TokenRateGateFactory(
        IOptionsMonitor<TokenRateGateOptions> optionsMonitor,
        ILoggerFactory loggerFactory)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _gates = new ConcurrentDictionary<string, Core.TokenRateGate>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets an existing TokenRateGate instance by name, or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="name">The unique identifier for this rate gate instance</param>
    /// <returns>The TokenRateGate instance associated with the specified name</returns>
    public ITokenRateGate GetOrCreate(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty or whitespace", nameof(name));

        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGateFactory));

        return _gates.GetOrAdd(name, CreateGate);
    }

    /// <summary>
    /// Gets an existing TokenRateGate instance by name, or null if it doesn't exist.
    /// </summary>
    /// <param name="name">The unique identifier for the rate gate instance</param>
    /// <returns>The TokenRateGate instance if it exists, or null</returns>
    public ITokenRateGate? Get(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty or whitespace", nameof(name));

        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGateFactory));

        _gates.TryGetValue(name, out var gate);
        return gate;
    }

    /// <summary>
    /// Checks whether a TokenRateGate instance with the specified name exists.
    /// </summary>
    /// <param name="name">The unique identifier to check</param>
    /// <returns>True if an instance with the specified name exists, false otherwise</returns>
    public bool Exists(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty or whitespace", nameof(name));

        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGateFactory));

        return _gates.ContainsKey(name);
    }

    /// <summary>
    /// Gets all active TokenRateGate instance names that have been created by this factory.
    /// </summary>
    /// <returns>An enumerable of all instance names</returns>
    public IEnumerable<string> GetAllNames()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TokenRateGateFactory));

        return _gates.Keys.ToList();
    }

    private Core.TokenRateGate CreateGate(string name)
    {
        var options = _optionsMonitor.Get(name);
        var logger = _loggerFactory.CreateLogger<Core.TokenRateGate>();
        return new Core.TokenRateGate(options, logger);
    }

    /// <summary>
    /// Disposes all managed TokenRateGate instances.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var gate in _gates.Values)
        {
            try
            {
                gate.Dispose();
            }
            catch
            {
                // Suppress exceptions during disposal
            }
        }

        _gates.Clear();
    }
}
