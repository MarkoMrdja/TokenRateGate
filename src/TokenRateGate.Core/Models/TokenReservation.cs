namespace TokenRateGate.Core.Models;

public class TokenReservation : IAsyncDisposable
{
    private readonly Func<TokenReservation, Task> _releaseFunc;
    private bool _disposed = false;
    
    public Guid Id { get; }
    public int ReservedTokens { get; }
    public int InputTokens { get; }
    public int? ActualTokensUsed { get; private set; }
    public DateTime CreatedAt { get; }

    internal TokenReservation(Guid id, int reservedTokens, int inputTokens, Func<TokenReservation, Task> releaseFunc)
    {
        Id = id;
        ReservedTokens = reservedTokens;
        InputTokens = inputTokens;
        CreatedAt = DateTime.UtcNow;
        _releaseFunc = releaseFunc ?? throw new ArgumentNullException(nameof(releaseFunc));
    }

    /// <summary>
    /// Records the actual tokens used for this request
    /// Call this after receiving the LLM response to track accurate usage
    /// </summary>
    /// <param name="actualTokensUsed">Total tokens used (input + output)</param>
    public void RecordActualUsage(int actualTokensUsed)
    {
        if (actualTokensUsed < 0)
            throw new ArgumentException("Actual tokens cannot be negative", nameof(actualTokensUsed));
        
        ActualTokensUsed = actualTokensUsed;
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