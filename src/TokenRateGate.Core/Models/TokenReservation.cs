using TokenRateGate.Abstractions;

namespace TokenRateGate.Core.Models;

public class TokenReservation : ITokenReservation
{
    private readonly Func<TokenReservation, Task> _releaseFunc;
    private bool _disposed = false;
    
    public Guid Id { get; }
    public int ReservedTokens { get; }
    public int InputTokens { get; }
    public int? ActualTokensUsed { get; private set; }
    private DateTime CreatedAtUtc { get; }

    DateTimeOffset ITokenReservation.CreatedAt => new DateTimeOffset(CreatedAtUtc, TimeSpan.Zero);

    internal TokenReservation(Guid id, int reservedTokens, int inputTokens, Func<TokenReservation, Task> releaseFunc)
    {
        Id = id;
        ReservedTokens = reservedTokens;
        InputTokens = inputTokens;
        CreatedAtUtc = DateTime.UtcNow;
        _releaseFunc = releaseFunc ?? throw new ArgumentNullException(nameof(releaseFunc));
    }

    /// <summary>
    /// Records the actual tokens used for this request
    /// Call this after receiving the LLM response to track accurate usage
    /// </summary>
    /// <param name="actualInputTokens">Actual input tokens consumed</param>
    /// <param name="actualOutputTokens">Actual output tokens generated</param>
    public void RecordActualUsage(int actualInputTokens, int actualOutputTokens)
    {
        if (actualInputTokens < 0)
            throw new ArgumentException("Actual input tokens cannot be negative", nameof(actualInputTokens));

        if (actualOutputTokens < 0)
            throw new ArgumentException("Actual output tokens cannot be negative", nameof(actualOutputTokens));

        ActualTokensUsed = actualInputTokens + actualOutputTokens;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _releaseFunc(this);
        }
    }
}