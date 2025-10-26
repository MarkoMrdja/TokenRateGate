namespace TokenRateGate.Abstractions;

/// <summary>
/// Represents a generic rate-limited client that wraps LLM provider calls
/// with automatic token reservation, usage tracking, and rate limit enforcement.
/// </summary>
/// <typeparam name="TRequest">The type of request object for the LLM provider</typeparam>
/// <typeparam name="TResponse">The type of response object from the LLM provider</typeparam>
public interface IRateLimitedClient<in TRequest, TResponse>
{
    /// <summary>
    /// Executes a request to the LLM provider with automatic rate limiting.
    /// This method will:
    /// 1. Estimate token usage for the request
    /// 2. Reserve capacity in the rate limiter
    /// 3. Execute the request
    /// 4. Record actual token usage
    /// 5. Release the reservation
    /// </summary>
    /// <param name="request">The request to send to the LLM provider</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The response from the LLM provider</returns>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled or times out waiting for capacity
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when rate limiting cannot be applied (e.g., configuration errors)
    /// </exception>
    Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a request to the LLM provider with automatic rate limiting and streaming support.
    /// This method follows the same reservation pattern as ExecuteAsync but is designed for
    /// streaming responses where tokens are generated incrementally.
    /// </summary>
    /// <param name="request">The request to send to the LLM provider</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>
    /// An async enumerable that yields response chunks as they are received.
    /// The reservation is held for the entire duration of streaming and released when complete.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled or times out waiting for capacity
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when rate limiting cannot be applied (e.g., configuration errors)
    /// </exception>
    IAsyncEnumerable<TResponse> ExecuteStreamingAsync(
        TRequest request,
        CancellationToken cancellationToken = default);
}
