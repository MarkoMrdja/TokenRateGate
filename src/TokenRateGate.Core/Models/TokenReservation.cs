using TokenRateGate.Abstractions;

namespace TokenRateGate.Core.Models;

/// <summary>
/// Represents a reservation of token capacity that must be disposed after use.
/// </summary>
/// <remarks>
/// TokenReservation holds capacity in the rate limiter until disposed.
/// Always call <see cref="RecordActualUsage"/> with the actual token counts from your API response
/// to ensure accurate capacity tracking. Dispose the reservation as soon as your operation completes
/// to free capacity for other requests.
/// </remarks>
/// <example>
/// <code>
/// var reservation = await gate.ReserveTokensAsync(inputTokens: 100, estimatedOutputTokens: 300);
/// await using var _ = reservation; // Ensures disposal
///
/// try
/// {
///     var response = await CallOpenAI();
///     reservation.RecordActualUsage(response.Usage.InputTokens, response.Usage.OutputTokens);
/// }
/// catch
/// {
///     // Reservation will still be released on disposal
///     throw;
/// }
/// </code>
/// </example>
public class TokenReservation : ITokenReservation
{
    private readonly Func<TokenReservation, Task> _releaseFunc;
    private readonly Action<TokenReservation, int>? _recordActualUsageCallback;
    private readonly SemaphoreSlim? _concurrencySemaphore;
    private int _disposed = 0; // Use int for Interlocked operations (0 = not disposed, 1 = disposed)
    private int _actualUsageRecorded = 0; // Use int for Interlocked operations (0 = not recorded, 1 = recorded)

    public Guid Id { get; }
    public int ReservedTokens { get; }
    public int InputTokens { get; }
    public int? ActualTokensUsed { get; private set; }
    private DateTime CreatedAtUtc { get; }

    DateTimeOffset ITokenReservation.CreatedAt => new DateTimeOffset(CreatedAtUtc, TimeSpan.Zero);

    internal TokenReservation(Guid id, int reservedTokens, int inputTokens, Func<TokenReservation, Task> releaseFunc, SemaphoreSlim? concurrencySemaphore = null, Action<TokenReservation, int>? recordActualUsageCallback = null)
    {
        Id = id;
        ReservedTokens = reservedTokens;
        InputTokens = inputTokens;
        CreatedAtUtc = DateTime.UtcNow;
        _releaseFunc = releaseFunc ?? throw new ArgumentNullException(nameof(releaseFunc));
        _concurrencySemaphore = concurrencySemaphore;
        _recordActualUsageCallback = recordActualUsageCallback;
    }

    /// <summary>
    /// Records the total actual tokens used for this request.
    /// Call this after receiving the LLM response when you have the total token count.
    /// This immediately updates the statistics for real-time monitoring.
    /// </summary>
    /// <param name="totalActualTokens">Total tokens consumed (input + output)</param>
    /// <remarks>
    /// This method is thread-safe and idempotent. If called multiple times, only the first call will be recorded.
    /// Use this overload when you only have total tokens from the API response (most common case).
    /// </remarks>
    public void RecordActualUsage(int totalActualTokens)
    {
        if (totalActualTokens < 0)
            throw new ArgumentException("Total tokens cannot be negative", nameof(totalActualTokens));

        // Use Interlocked to ensure only one thread can record usage
        // This prevents statistics corruption from concurrent calls
        if (Interlocked.CompareExchange(ref _actualUsageRecorded, 1, 0) == 0)
        {
            ActualTokensUsed = totalActualTokens;

            // Notify the gate immediately so stats are updated in real-time
            _recordActualUsageCallback?.Invoke(this, totalActualTokens);
        }
    }

    /// <summary>
    /// Records the actual tokens used for this request with separate input/output counts.
    /// Call this after receiving the LLM response to track accurate usage.
    /// This immediately updates the statistics for real-time monitoring.
    /// </summary>
    /// <param name="actualInputTokens">Actual input tokens consumed</param>
    /// <param name="actualOutputTokens">Actual output tokens generated</param>
    /// <remarks>
    /// This method is thread-safe and idempotent. If called multiple times, only the first call will be recorded.
    /// Use this overload when you need detailed tracking of input vs output tokens.
    /// </remarks>
    public void RecordActualUsage(int actualInputTokens, int actualOutputTokens)
    {
        if (actualInputTokens < 0)
            throw new ArgumentException("Actual input tokens cannot be negative", nameof(actualInputTokens));

        if (actualOutputTokens < 0)
            throw new ArgumentException("Actual output tokens cannot be negative", nameof(actualOutputTokens));

        int totalActual = actualInputTokens + actualOutputTokens;
        RecordActualUsage(totalActual);
    }

    /// <summary>
    /// Disposes the reservation and releases capacity back to the rate limiter.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and idempotent. Multiple concurrent calls will be safely handled,
    /// with only the first call performing the actual disposal. This prevents semaphore corruption
    /// and double-release of resources.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Use Interlocked.CompareExchange for atomic check-and-set
        // Only the first thread to see _disposed=0 will proceed with disposal
        // This prevents double-release of the semaphore which would throw SemaphoreFullException
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            await _releaseFunc(this);

            // Release concurrency semaphore if it was held
            _concurrencySemaphore?.Release();
        }
    }
}