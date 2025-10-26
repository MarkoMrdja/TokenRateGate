using TokenRateGate.Core;
using TokenRateGate.Core.Models;

namespace TokenRateGate.Extensions;

/// <summary>
/// Extension methods for working with TokenRateGate.
/// </summary>
public static class TokenRateGateExtensions
{
    /// <summary>
    /// Executes an action with automatic token reservation and release.
    /// This provides a convenient pattern for wrapping arbitrary LLM calls.
    /// </summary>
    /// <param name="rateGate">The rate gate instance</param>
    /// <param name="estimatedTokens">The estimated number of tokens for the operation</param>
    /// <param name="action">The action to execute with the reservation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <exception cref="ArgumentNullException">Thrown when rateGate or action is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when estimatedTokens is less than or equal to zero</exception>
    public static async Task ExecuteWithReservationAsync(
        this Core.TokenRateGate rateGate,
        int estimatedTokens,
        Func<TokenReservation, Task> action,
        CancellationToken cancellationToken = default)
    {
        if (rateGate == null)
            throw new ArgumentNullException(nameof(rateGate));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        if (estimatedTokens <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(estimatedTokens),
                "Estimated tokens must be greater than zero");

        await using var reservation = await rateGate.ReserveTokensAsync(estimatedTokens, 0, cancellationToken);
        await action(reservation);
    }

    /// <summary>
    /// Executes a function with automatic token reservation and release.
    /// This provides a convenient pattern for wrapping arbitrary LLM calls that return a value.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the function</typeparam>
    /// <param name="rateGate">The rate gate instance</param>
    /// <param name="estimatedTokens">The estimated number of tokens for the operation</param>
    /// <param name="func">The function to execute with the reservation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the function</returns>
    /// <exception cref="ArgumentNullException">Thrown when rateGate or func is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when estimatedTokens is less than or equal to zero</exception>
    public static async Task<TResult> ExecuteWithReservationAsync<TResult>(
        this Core.TokenRateGate rateGate,
        int estimatedTokens,
        Func<TokenReservation, Task<TResult>> func,
        CancellationToken cancellationToken = default)
    {
        if (rateGate == null)
            throw new ArgumentNullException(nameof(rateGate));

        if (func == null)
            throw new ArgumentNullException(nameof(func));

        if (estimatedTokens <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(estimatedTokens),
                "Estimated tokens must be greater than zero");

        await using var reservation = await rateGate.ReserveTokensAsync(estimatedTokens, 0, cancellationToken);
        return await func(reservation);
    }

    /// <summary>
    /// Gets the current capacity percentage (0-100) available in the rate gate.
    /// </summary>
    /// <param name="rateGate">The rate gate instance</param>
    /// <returns>The percentage of capacity available (0-100)</returns>
    /// <exception cref="ArgumentNullException">Thrown when rateGate is null</exception>
    public static double GetCapacityPercentage(this Core.TokenRateGate rateGate)
    {
        if (rateGate == null)
            throw new ArgumentNullException(nameof(rateGate));

        var stats = rateGate.GetUsageStats();
        var totalCapacity = stats.CurrentUsage + stats.AvailableCapacity;
        return totalCapacity > 0 ? (double)stats.AvailableCapacity / totalCapacity * 100.0 : 100.0;
    }

    /// <summary>
    /// Checks if the rate gate is near capacity (less than specified percentage available).
    /// </summary>
    /// <param name="rateGate">The rate gate instance</param>
    /// <param name="thresholdPercentage">The threshold percentage (default: 10%)</param>
    /// <returns>True if available capacity is below the threshold, false otherwise</returns>
    /// <exception cref="ArgumentNullException">Thrown when rateGate is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when thresholdPercentage is not between 0 and 100</exception>
    public static bool IsNearCapacity(this Core.TokenRateGate rateGate, double thresholdPercentage = 10.0)
    {
        if (rateGate == null)
            throw new ArgumentNullException(nameof(rateGate));

        if (thresholdPercentage < 0 || thresholdPercentage > 100)
            throw new ArgumentOutOfRangeException(
                nameof(thresholdPercentage),
                "Threshold percentage must be between 0 and 100");

        return rateGate.GetCapacityPercentage() < thresholdPercentage;
    }

    /// <summary>
    /// Waits until the rate gate has at least the specified percentage of capacity available.
    /// </summary>
    /// <param name="rateGate">The rate gate instance</param>
    /// <param name="minimumPercentage">The minimum percentage of capacity required (default: 20%)</param>
    /// <param name="checkInterval">How often to check capacity (default: 100ms)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the capacity threshold is met</returns>
    /// <exception cref="ArgumentNullException">Thrown when rateGate is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when minimumPercentage is not between 0 and 100</exception>
    public static async Task WaitForCapacityAsync(
        this Core.TokenRateGate rateGate,
        double minimumPercentage = 20.0,
        TimeSpan? checkInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (rateGate == null)
            throw new ArgumentNullException(nameof(rateGate));

        if (minimumPercentage < 0 || minimumPercentage > 100)
            throw new ArgumentOutOfRangeException(
                nameof(minimumPercentage),
                "Minimum percentage must be between 0 and 100");

        var interval = checkInterval ?? TimeSpan.FromMilliseconds(100);

        while (rateGate.GetCapacityPercentage() < minimumPercentage)
        {
            await Task.Delay(interval, cancellationToken);
        }
    }
}
