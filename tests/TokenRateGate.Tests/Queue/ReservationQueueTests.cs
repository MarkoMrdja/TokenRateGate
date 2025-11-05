using Microsoft.Extensions.Logging.Abstractions;
using TokenRateGate.Core.Models;
using TokenRateGate.Core.Queue;
using Xunit;

namespace TokenRateGate.Tests.Queue;

public class ReservationQueueTests
{
    private readonly ReservationQueue _queue;

    public ReservationQueueTests()
    {
        _queue = new ReservationQueue(NullLogger.Instance);
    }

    [Fact]
    public void Count_WhenEmpty_ReturnsZero()
    {
        Assert.Equal(0, _queue.Count);
        Assert.False(_queue.HasWaitingRequests);
    }

    [Fact]
    public void AddToQueue_IncreasesCount()
    {
        var request = new WaitingRequest(100, CancellationToken.None);

        var registration = _queue.AddToQueue(request, CancellationToken.None, () => { });

        Assert.Equal(1, _queue.Count);
        Assert.True(_queue.HasWaitingRequests);

        registration.Dispose();
    }

    [Fact]
    public void AddToQueue_WithCancellation_RemovesRequest()
    {
        var cts = new CancellationTokenSource();
        var request = new WaitingRequest(100, cts.Token);
        bool cancelledCallbackInvoked = false;

        var registration = _queue.AddToQueue(
            request,
            cts.Token,
            () => cancelledCallbackInvoked = true);

        Assert.Equal(1, _queue.Count);

        // Cancel the token
        cts.Cancel();

        // Give cancellation callback time to execute
        Thread.Sleep(50);

        Assert.Equal(0, _queue.Count);
        Assert.True(cancelledCallbackInvoked);
        Assert.True(request.TaskCompletionSource.Task.IsCanceled);

        registration.Dispose();
        cts.Dispose();
    }

    [Fact]
    public void ProcessQueue_WhenEmpty_ReturnsEmptyList()
    {
        var granted = _queue.ProcessQueue(
            tokens => (false, Guid.Empty),
            _ => { });

        Assert.Empty(granted);
    }

    [Fact]
    public void ProcessQueue_WithCapacity_GrantsRequest()
    {
        var request = new WaitingRequest(100, CancellationToken.None);
        var registration = _queue.AddToQueue(request, CancellationToken.None, () => { });

        var expectedId = Guid.NewGuid();
        var granted = _queue.ProcessQueue(
            tokens => (true, expectedId),
            _ => { });

        Assert.Single(granted);
        Assert.Equal(request, granted[0]);
        Assert.Equal(expectedId, request.ReservationId);
        Assert.Equal(0, _queue.Count);

        registration.Dispose();
    }

    [Fact]
    public void ProcessQueue_WithoutCapacity_StopsProcessing()
    {
        var request1 = new WaitingRequest(100, CancellationToken.None);
        var request2 = new WaitingRequest(200, CancellationToken.None);

        var registration1 = _queue.AddToQueue(request1, CancellationToken.None, () => { });
        var registration2 = _queue.AddToQueue(request2, CancellationToken.None, () => { });

        int insufficientCapacityTokens = 0;
        var granted = _queue.ProcessQueue(
            tokens => (false, Guid.Empty),
            tokens => insufficientCapacityTokens = tokens);

        Assert.Empty(granted);
        Assert.Equal(100, insufficientCapacityTokens); // First request's token count
        Assert.Equal(2, _queue.Count); // Both still in queue

        registration1.Dispose();
        registration2.Dispose();
    }

    [Fact]
    public void ProcessQueue_GrantsPartialRequests_StopsAtFirstFailure()
    {
        var request1 = new WaitingRequest(100, CancellationToken.None);
        var request2 = new WaitingRequest(200, CancellationToken.None);
        var request3 = new WaitingRequest(300, CancellationToken.None);

        var registration1 = _queue.AddToQueue(request1, CancellationToken.None, () => { });
        var registration2 = _queue.AddToQueue(request2, CancellationToken.None, () => { });
        var registration3 = _queue.AddToQueue(request3, CancellationToken.None, () => { });

        int callCount = 0;
        var granted = _queue.ProcessQueue(
            tokens =>
            {
                callCount++;
                // Grant first two, fail on third
                if (callCount <= 2)
                    return (true, Guid.NewGuid());
                return (false, Guid.Empty);
            },
            _ => { });

        Assert.Equal(2, granted.Count);
        Assert.Equal(1, _queue.Count); // Third request still waiting
        Assert.Contains(request1, granted);
        Assert.Contains(request2, granted);

        registration1.Dispose();
        registration2.Dispose();
        registration3.Dispose();
    }

    [Fact]
    public void ProcessQueue_SkipsCancelledRequests()
    {
        var cts = new CancellationTokenSource();
        var request1 = new WaitingRequest(100, cts.Token);
        var request2 = new WaitingRequest(200, CancellationToken.None);

        var registration1 = _queue.AddToQueue(request1, cts.Token, () => { });
        var registration2 = _queue.AddToQueue(request2, CancellationToken.None, () => { });

        // Cancel first request
        cts.Cancel();
        Thread.Sleep(50); // Let cancellation callback run

        var granted = _queue.ProcessQueue(
            tokens => (true, Guid.NewGuid()),
            _ => { });

        // Should only grant the second request
        Assert.Single(granted);
        Assert.Equal(request2, granted[0]);
        Assert.Equal(0, _queue.Count);

        registration1.Dispose();
        registration2.Dispose();
        cts.Dispose();
    }

    [Fact]
    public void ProcessQueue_RemovesCancelledRequestsDuringProcessing()
    {
        var cts = new CancellationTokenSource();
        var request1 = new WaitingRequest(100, cts.Token);
        var request2 = new WaitingRequest(200, CancellationToken.None);

        var registration1 = _queue.AddToQueue(request1, cts.Token, () => { });
        var registration2 = _queue.AddToQueue(request2, CancellationToken.None, () => { });

        // Don't trigger cancellation callback - just mark token as cancelled
        cts.Cancel();

        var granted = _queue.ProcessQueue(
            tokens => (true, Guid.NewGuid()),
            _ => { });

        // First request should be skipped, second granted
        Assert.Single(granted);
        Assert.Equal(request2, granted[0]);
        Assert.Equal(0, _queue.Count);

        registration1.Dispose();
        registration2.Dispose();
        cts.Dispose();
    }

    [Fact]
    public void CancelAll_CancelsAllRequests()
    {
        var request1 = new WaitingRequest(100, CancellationToken.None);
        var request2 = new WaitingRequest(200, CancellationToken.None);

        var registration1 = _queue.AddToQueue(request1, CancellationToken.None, () => { });
        var registration2 = _queue.AddToQueue(request2, CancellationToken.None, () => { });

        _queue.CancelAll();

        Assert.Equal(0, _queue.Count);
        Assert.True(request1.TaskCompletionSource.Task.IsCanceled);
        Assert.True(request2.TaskCompletionSource.Task.IsCanceled);

        registration1.Dispose();
        registration2.Dispose();
    }

    [Fact]
    public void ProcessQueue_MaintainsFIFOOrder()
    {
        var grantedOrder = new List<int>();
        var request1 = new WaitingRequest(100, CancellationToken.None);
        var request2 = new WaitingRequest(200, CancellationToken.None);
        var request3 = new WaitingRequest(300, CancellationToken.None);

        var registration1 = _queue.AddToQueue(request1, CancellationToken.None, () => { });
        var registration2 = _queue.AddToQueue(request2, CancellationToken.None, () => { });
        var registration3 = _queue.AddToQueue(request3, CancellationToken.None, () => { });

        var granted = _queue.ProcessQueue(
            tokens =>
            {
                grantedOrder.Add(tokens);
                return (true, Guid.NewGuid());
            },
            _ => { });

        Assert.Equal(3, granted.Count);
        Assert.Equal(new[] { 100, 200, 300 }, grantedOrder); // FIFO order

        registration1.Dispose();
        registration2.Dispose();
        registration3.Dispose();
    }
}
