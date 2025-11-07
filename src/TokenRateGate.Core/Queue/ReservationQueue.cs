using Microsoft.Extensions.Logging;
using TokenRateGate.Core.Models;

namespace TokenRateGate.Core.Queue;

/// <summary>
/// Manages the queue of reservation requests waiting for capacity.
/// Thread-safe when used with external locking.
/// </summary>
internal sealed class ReservationQueue
{
    private readonly LinkedList<WaitingRequest> _waitingRequests = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Gets the current number of requests waiting in the queue.
    /// </summary>
    public int Count => _waitingRequests.Count;

    /// <summary>
    /// Gets whether the queue has any waiting requests.
    /// </summary>
    public bool HasWaitingRequests => _waitingRequests.Count > 0;

    public ReservationQueue(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds a request to the end of the waiting queue and registers cancellation handling.
    /// Must be called within a lock.
    /// </summary>
    /// <param name="request">The waiting request to add.</param>
    /// <param name="effectiveCancellationToken">The cancellation token to monitor.</param>
    /// <param name="onCancelled">Callback invoked when the request is cancelled (called under lock).</param>
    /// <returns>A cancellation token registration that must be disposed.</returns>
    public CancellationTokenRegistration AddToQueue(
        WaitingRequest request,
        CancellationToken effectiveCancellationToken,
        Action onCancelled)
    {
        var node = _waitingRequests.AddLast(request);
        request.Node = node;

        // Register cancellation callback - it will acquire the lock internally
        var registration = effectiveCancellationToken.Register(() =>
        {
            // Only cancel if still in queue (node not null and still belongs to this list)
            if (request.Node != null && request.Node.List == _waitingRequests)
            {
                _waitingRequests.Remove(request.Node);
                request.Node = null;

                // Set cancelled AFTER removing from queue to prevent race
                request.TaskCompletionSource.TrySetCanceled(effectiveCancellationToken);

                _logger.LogDebug("Cancelled waiting request: {TotalTokens} tokens", request.RequiredTokens);

                // Notify caller (e.g., to update metrics and timer state)
                onCancelled();
            }
        });

        return registration;
    }

    /// <summary>
    /// Processes waiting requests in FIFO order, attempting to grant capacity to each.
    /// Must be called within a lock.
    /// </summary>
    /// <param name="tryReserveFunc">Function that attempts to reserve capacity for a request. Returns true and reservationId if successful.</param>
    /// <param name="onInsufficientCapacity">Callback invoked when capacity is insufficient (called with required tokens).</param>
    /// <returns>List of requests that were granted capacity (caller must complete their TaskCompletionSource outside the lock).</returns>
    public List<WaitingRequest> ProcessQueue(
        Func<int, (bool success, Guid reservationId)> tryReserveFunc,
        Action<int> onInsufficientCapacity)
    {
        var grantedRequests = new List<WaitingRequest>();

        if (_waitingRequests.Count == 0)
            return grantedRequests;

        var currentNode = _waitingRequests.First;

        while (currentNode != null)
        {
            var request = currentNode.Value;
            var nextNode = currentNode.Next;

            // Check cancellation - if cancelled, remove and skip
            if (request.CancellationToken.IsCancellationRequested)
            {
                _waitingRequests.Remove(currentNode);
                request.Node = null;
                // Don't set result here - the cancellation callback already did it

                _logger.LogDebug("Removed cancelled request: {RequiredTokens} tokens", request.RequiredTokens);

                currentNode = nextNode;
                continue;
            }

            // Try to grant this request
            var (success, reservationId) = tryReserveFunc(request.RequiredTokens);

            if (success)
            {
                _waitingRequests.Remove(currentNode);
                request.Node = null;
                request.ReservationId = reservationId;
                grantedRequests.Add(request);

                _logger.LogDebug("Granted waiting request: {RequiredTokens} tokens with ID {ReservationID} (queue: {Remaining})",
                    request.RequiredTokens, reservationId, _waitingRequests.Count);
            }
            else
            {
                // No more capacity available, stop processing
                _logger.LogDebug("Insufficient capacity for {RequiredTokens} tokens, stopping queue processing",
                    request.RequiredTokens);

                onInsufficientCapacity(request.RequiredTokens);
                break;
            }

            currentNode = nextNode;
        }

        return grantedRequests;
    }

    /// <summary>
    /// Removes all waiting requests and cancels them.
    /// Should be called during disposal. Must be called within a lock.
    /// </summary>
    public void CancelAll()
    {
        foreach (var request in _waitingRequests)
        {
            request.TaskCompletionSource.TrySetCanceled();
        }
        _waitingRequests.Clear();
    }
}
