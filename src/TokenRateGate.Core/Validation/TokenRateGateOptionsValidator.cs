using Microsoft.Extensions.Logging;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Core.Validation;

/// <summary>
/// Validates TokenRateGateOptions configuration.
/// </summary>
internal static class TokenRateGateOptionsValidator
{
    public static void Validate(TokenRateGateOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        ValidateTokenLimit(options);
        ValidateWindowSeconds(options);
        ValidateSafetyBuffer(options, logger);
        ValidateConcurrency(options);
        ValidateRequestLimits(options);
        ValidateMaxWaitTime(options);
        ValidateOutputEstimation(options);
    }

    private static void ValidateTokenLimit(TokenRateGateOptions options)
    {
        if (options.TokenLimit <= 0)
            throw new ArgumentException("TokenLimit must be positive", nameof(options.TokenLimit));
    }

    private static void ValidateWindowSeconds(TokenRateGateOptions options)
    {
        if (options.WindowSeconds <= 0)
            throw new ArgumentException("WindowSeconds must be positive", nameof(options.WindowSeconds));
    }

    private static void ValidateSafetyBuffer(TokenRateGateOptions options, ILogger? logger)
    {
        if (options.SafetyBufferPercentage < 0)
            throw new ArgumentException(
                "SafetyBufferPercentage cannot be negative",
                nameof(options.SafetyBufferPercentage));

        if (options.SafetyBufferPercentage >= 1.0)
            throw new ArgumentException(
                "SafetyBufferPercentage must be less than 1.0 (100%)",
                nameof(options.SafetyBufferPercentage));

        if (options.SafetyBufferPercentage > 0.5)
            logger?.LogWarning(
                "SafetyBufferPercentage ({SafetyBufferPercentage:P0}) is more than 50% of TokenLimit. " +
                "This leaves very little usable capacity and may cause frequent queuing.",
                options.SafetyBufferPercentage);
    }

    private static void ValidateConcurrency(TokenRateGateOptions options)
    {
        if (options.MaxConcurrentRequests <= 0)
            throw new ArgumentException(
                "MaxConcurrentRequests must be positive",
                nameof(options.MaxConcurrentRequests));

        if (options.MaxConcurrentRequests > 10_000)
            throw new ArgumentException(
                $"MaxConcurrentRequests ({options.MaxConcurrentRequests:N0}) exceeds safe limit (10,000). " +
                $"This could lead to resource exhaustion. The semaphore controls both active reservations " +
                $"and queued requests, so excessive values can cause memory exhaustion.",
                nameof(options.MaxConcurrentRequests));
    }

    private static void ValidateRequestLimits(TokenRateGateOptions options)
    {
        if (options.MaxRequestsPerMinute <= 0)
            throw new ArgumentException(
                "MaxRequestsPerMinute must be positive",
                nameof(options.MaxRequestsPerMinute));

        if (options.RequestWindowSeconds <= 0)
            throw new ArgumentException(
                "RequestWindowSeconds must be positive",
                nameof(options.RequestWindowSeconds));
    }

    private static void ValidateMaxWaitTime(TokenRateGateOptions options)
    {
        if (!options.MaxWaitTime.HasValue)
            return;

        if (options.MaxWaitTime.Value <= TimeSpan.Zero)
            throw new ArgumentException(
                "MaxWaitTime must be positive when set",
                nameof(options.MaxWaitTime));

        if (options.MaxWaitTime.Value > TimeSpan.FromHours(24))
            throw new ArgumentException(
                "MaxWaitTime cannot exceed 24 hours for practical use",
                nameof(options.MaxWaitTime));
    }

    private static void ValidateOutputEstimation(TokenRateGateOptions options)
    {
        if (options.OutputMultiplier < 0)
            throw new ArgumentException(
                "OutputMultiplier cannot be negative",
                nameof(options.OutputMultiplier));

        if (options.DefaultOutputTokens < 0)
            throw new ArgumentException(
                "DefaultOutputTokens cannot be negative",
                nameof(options.DefaultOutputTokens));

        if (options.DefaultOutputTokens > options.TokenLimit)
            throw new ArgumentException(
                $"DefaultOutputTokens ({options.DefaultOutputTokens}) cannot exceed TokenLimit ({options.TokenLimit})",
                nameof(options.DefaultOutputTokens));
    }
}
