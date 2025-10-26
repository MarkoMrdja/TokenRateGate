using Microsoft.Extensions.Logging;
using NSubstitute;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Core.Tests.TestHelpers;

/// <summary>
/// Factory for creating TokenRateGate instances with various test configurations
/// </summary>
public static class TokenRateGateTestFactory
{
    /// <summary>
    /// Creates a minimal configuration for basic testing
    /// </summary>
    public static TokenRateGateOptions CreateMinimalOptions()
    {
        return new TokenRateGateOptions
        {
            TokenLimit = 10000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.10, // 10% safety buffer (1000 tokens)
            MaxConcurrentRequests = 1,
            MaxRequestsPerMinute = 100,
            RequestWindowSeconds = 60,
            MaxWaitTime = TimeSpan.FromSeconds(5), // Short timeout for faster tests
            OutputEstimationStrategy = Utils.OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5,
            DefaultOutputTokens = 1000
        };
    }

    /// <summary>
    /// Creates configuration for testing high concurrency scenarios
    /// </summary>
    public static TokenRateGateOptions CreateHighConcurrencyOptions(int maxConcurrent = 10)
    {
        return new TokenRateGateOptions
        {
            TokenLimit = 100000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.05, // 5% safety buffer (5000 tokens)
            MaxConcurrentRequests = maxConcurrent,
            MaxRequestsPerMinute = 1000,
            RequestWindowSeconds = 60,
            MaxWaitTime = TimeSpan.FromSeconds(10),
            OutputEstimationStrategy = Utils.OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5,
            DefaultOutputTokens = 1000
        };
    }

    /// <summary>
    /// Creates configuration for testing race conditions with tight timing
    /// </summary>
    public static TokenRateGateOptions CreateRaceConditionTestOptions()
    {
        return new TokenRateGateOptions
        {
            TokenLimit = 50000,
            WindowSeconds = 1, // Very short window to trigger frequent cleanup
            SafetyBufferPercentage = 0.10, // 10% safety buffer (5000 tokens)
            MaxConcurrentRequests = 5,
            MaxRequestsPerMinute = 100,
            RequestWindowSeconds = 1,
            MaxWaitTime = TimeSpan.FromSeconds(5),
            OutputEstimationStrategy = Utils.OutputEstimationStrategy.FixedMultiplier,
            OutputMultiplier = 0.5,
            DefaultOutputTokens = 1000
        };
    }

    /// <summary>
    /// Creates configuration for testing near-capacity scenarios
    /// </summary>
    public static TokenRateGateOptions CreateNearCapacityOptions()
    {
        return new TokenRateGateOptions
        {
            TokenLimit = 10000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.20, // 20% safety buffer (2000 tokens)
            MaxConcurrentRequests = 2,
            MaxRequestsPerMinute = 50,
            RequestWindowSeconds = 60,
            MaxWaitTime = TimeSpan.FromSeconds(5),
            OutputEstimationStrategy = Utils.OutputEstimationStrategy.Conservative,
            OutputMultiplier = 0.5,
            DefaultOutputTokens = 1000
        };
    }

    /// <summary>
    /// Creates a substitute logger for testing
    /// </summary>
    public static ILogger<TokenRateGate> CreateLogger()
    {
        return Substitute.For<ILogger<TokenRateGate>>();
    }

    /// <summary>
    /// Creates a TokenRateGate instance with minimal configuration
    /// </summary>
    public static TokenRateGate CreateMinimalGate()
    {
        return new TokenRateGate(CreateMinimalOptions(), CreateLogger());
    }

    /// <summary>
    /// Creates a TokenRateGate instance with custom options
    /// </summary>
    public static TokenRateGate CreateGate(TokenRateGateOptions options)
    {
        return new TokenRateGate(options, CreateLogger());
    }

    /// <summary>
    /// Creates a TokenRateGate instance with custom options and logger
    /// </summary>
    public static TokenRateGate CreateGate(TokenRateGateOptions options, ILogger<TokenRateGate> logger)
    {
        return new TokenRateGate(options, logger);
    }
}
