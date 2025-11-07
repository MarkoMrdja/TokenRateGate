namespace TokenRateGate.Core.Models;

/// <summary>
/// Represents an active reservation that holds capacity but hasn't completed yet.
/// Used internally by TokenRateGate to track in-flight requests.
/// </summary>
internal sealed class PendingReservations
{
    public Guid Id { get; }
    public int EstimatedTokens { get; }
    public DateTime Timestamp { get; }
    public int? ActualTokensUsed { get; set; }

    public PendingReservations(Guid id, int estimatedTokens, DateTime timestamp)
    {
        Id = id;
        EstimatedTokens = estimatedTokens;
        Timestamp = timestamp;
        ActualTokensUsed = null;
    }
}
