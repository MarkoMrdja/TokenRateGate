using TokenRateGate.Abstractions;

namespace TokenRateGate.Core.Models;

public class TokenReservation : ITokenReservation
{
    private readonly Func<TokenReservation, Task> _releaseFunc;
    private readonly Action<TokenReservation, int>? _recordActualUsageCallback;
    private readonly SemaphoreSlim? _concurrencySemaphore;
    private bool _disposed = false;

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
    /// Records the actual tokens used for this request
    /// Call this after receiving the LLM response to track accurate usage
    /// This immediately updates the statistics for real-time monitoring
    /// </summary>
    /// <param name="actualInputTokens">Actual input tokens consumed</param>
    /// <param name="actualOutputTokens">Actual output tokens generated</param>
    public void RecordActualUsage(int actualInputTokens, int actualOutputTokens)
    {
        if (actualInputTokens < 0)
            throw new ArgumentException("Actual input tokens cannot be negative", nameof(actualInputTokens));

        if (actualOutputTokens < 0)
            throw new ArgumentException("Actual output tokens cannot be negative", nameof(actualOutputTokens));

        int totalActual = actualInputTokens + actualOutputTokens;
        ActualTokensUsed = totalActual;

        // Notify the gate immediately so stats are updated in real-time
        _recordActualUsageCallback?.Invoke(this, totalActual);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _releaseFunc(this);

            // Release concurrency semaphore if it was held
            _concurrencySemaphore?.Release();
        }
    }
}