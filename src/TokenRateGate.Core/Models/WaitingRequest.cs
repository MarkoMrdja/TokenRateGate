namespace TokenRateGate.Core.Models;

/// <summary>
/// Represents a queued request waiting for capacity to become available.
/// Used internally by TokenRateGate for the waiting request queue.
/// </summary>
internal sealed class WaitingRequest
{
    public int RequiredTokens { get; }
    public CancellationToken CancellationToken { get; set; }
    public TaskCompletionSource<Guid> TaskCompletionSource { get; }
    public LinkedListNode<WaitingRequest>? Node { get; set; }
    public Guid ReservationId { get; set; }

    public WaitingRequest(int requiredTokens, CancellationToken cancellationToken)
    {
        RequiredTokens = requiredTokens;
        CancellationToken = cancellationToken;
        TaskCompletionSource = new TaskCompletionSource<Guid>();
    }
}
