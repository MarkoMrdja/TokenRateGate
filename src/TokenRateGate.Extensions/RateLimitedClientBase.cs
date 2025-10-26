using Microsoft.Extensions.Logging;
using TokenRateGate.Abstractions;
using TokenRateGate.Core;
using TokenRateGate.Core.Models;

namespace TokenRateGate.Extensions;

/// <summary>
/// Base class for implementing rate-limited LLM clients.
/// Provides automatic token estimation, reservation, and usage tracking.
/// </summary>
/// <typeparam name="TRequest">The type of request object for the LLM provider</typeparam>
/// <typeparam name="TResponse">The type of response object from the LLM provider</typeparam>
public abstract class RateLimitedClientBase<TRequest, TResponse> : IRateLimitedClient<TRequest, TResponse>
{
    private readonly Core.TokenRateGate _rateGate;
    private readonly ITokenEstimator<TRequest> _tokenEstimator;
    private readonly ITokenUsageExtractor<TResponse> _usageExtractor;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitedClientBase{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="rateGate">The rate gate instance to use for rate limiting</param>
    /// <param name="tokenEstimator">The token estimator for request estimation</param>
    /// <param name="usageExtractor">The usage extractor for response analysis</param>
    /// <param name="logger">The logger instance</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null</exception>
    protected RateLimitedClientBase(
        Core.TokenRateGate rateGate,
        ITokenEstimator<TRequest> tokenEstimator,
        ITokenUsageExtractor<TResponse> usageExtractor,
        ILogger logger)
    {
        _rateGate = rateGate ?? throw new ArgumentNullException(nameof(rateGate));
        _tokenEstimator = tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
        _usageExtractor = usageExtractor ?? throw new ArgumentNullException(nameof(usageExtractor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the actual request to the LLM provider.
    /// This method must be implemented by derived classes to handle the provider-specific logic.
    /// </summary>
    /// <param name="request">The request to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The response from the LLM provider</returns>
    protected abstract Task<TResponse> ExecuteRequestAsync(TRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Executes the actual streaming request to the LLM provider.
    /// This method must be implemented by derived classes to handle provider-specific streaming logic.
    /// Default implementation throws NotSupportedException.
    /// </summary>
    /// <param name="request">The request to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An async enumerable of response chunks</returns>
    protected virtual IAsyncEnumerable<TResponse> ExecuteStreamingRequestAsync(
        TRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"Streaming is not supported by {GetType().Name}. Override ExecuteStreamingRequestAsync to enable streaming.");
    }

    /// <inheritdoc />
    public async Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        // Estimate tokens
        var estimatedInputTokens = await _tokenEstimator.EstimateInputTokensAsync(request, cancellationToken);
        var estimatedOutputTokens = await _tokenEstimator.EstimateOutputTokensAsync(request, cancellationToken);

        _logger.LogDebug(
            "Estimated {InputTokens} input + {OutputTokens} output = {TotalTokens} tokens for request",
            estimatedInputTokens,
            estimatedOutputTokens,
            estimatedInputTokens + estimatedOutputTokens);

        // Reserve capacity
        TokenReservation? reservation = null;
        try
        {
            reservation = await _rateGate.ReserveTokensAsync(estimatedInputTokens, estimatedOutputTokens, cancellationToken);

            _logger.LogDebug(
                "Reserved {ReservedTokens} tokens (reservation {ReservationId})",
                reservation.ReservedTokens,
                reservation.Id);

            // Execute the request
            var response = await ExecuteRequestAsync(request, cancellationToken);

            // Extract actual usage and record it
            var actualInputTokens = _usageExtractor.ExtractInputTokens(response);
            var actualOutputTokens = _usageExtractor.ExtractOutputTokens(response);

            if (actualInputTokens.HasValue && actualOutputTokens.HasValue)
            {
                var actualTotal = actualInputTokens.Value + actualOutputTokens.Value;
                reservation.RecordActualUsage(actualInputTokens.Value, actualOutputTokens.Value);

                var efficiency = actualTotal > 0 ? (double)actualTotal / reservation.ReservedTokens : 0;

                _logger.LogDebug(
                    "Actual usage: {ActualTokens} tokens (input: {InputTokens}, output: {OutputTokens}), " +
                    "efficiency: {Efficiency:P1} (reservation {ReservationId})",
                    actualTotal,
                    actualInputTokens.Value,
                    actualOutputTokens.Value,
                    efficiency,
                    reservation.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Could not extract actual token usage from response (reservation {ReservationId})",
                    reservation.Id);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Request cancelled by user");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request timed out waiting for rate limit capacity");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error executing rate-limited request (reservation {ReservationId})",
                reservation?.Id);
            throw;
        }
        finally
        {
            if (reservation != null)
            {
                await reservation.DisposeAsync();
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TResponse> ExecuteStreamingAsync(
        TRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        // Estimate tokens
        var estimatedInputTokens = await _tokenEstimator.EstimateInputTokensAsync(request, cancellationToken);
        var estimatedOutputTokens = await _tokenEstimator.EstimateOutputTokensAsync(request, cancellationToken);

        _logger.LogDebug(
            "Estimated {InputTokens} input + {OutputTokens} output = {TotalTokens} tokens for streaming request",
            estimatedInputTokens,
            estimatedOutputTokens,
            estimatedInputTokens + estimatedOutputTokens);

        // Reserve capacity
        TokenReservation? reservation = null;
        try
        {
            reservation = await _rateGate.ReserveTokensAsync(estimatedInputTokens, estimatedOutputTokens, cancellationToken);

            _logger.LogDebug(
                "Reserved {ReservedTokens} tokens for streaming (reservation {ReservationId})",
                reservation.ReservedTokens,
                reservation.Id);

            // Track total usage across all chunks
            int? totalInputTokens = null;
            int? totalOutputTokens = null;
            var chunkCount = 0;

            // Stream responses
            await foreach (var chunk in ExecuteStreamingRequestAsync(request, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                chunkCount++;

                // Try to extract usage from each chunk (usually only available in the last chunk)
                var inputTokens = _usageExtractor.ExtractInputTokens(chunk);
                var outputTokens = _usageExtractor.ExtractOutputTokens(chunk);

                if (inputTokens.HasValue)
                    totalInputTokens = inputTokens.Value;

                if (outputTokens.HasValue)
                    totalOutputTokens = outputTokens.Value;

                yield return chunk;
            }

            // Record actual usage if available
            if (totalInputTokens.HasValue && totalOutputTokens.HasValue)
            {
                var actualTotal = totalInputTokens.Value + totalOutputTokens.Value;
                reservation.RecordActualUsage(totalInputTokens.Value, totalOutputTokens.Value);

                var efficiency = actualTotal > 0 ? (double)actualTotal / reservation.ReservedTokens : 0;

                _logger.LogDebug(
                    "Streaming completed: {ChunkCount} chunks, {ActualTokens} tokens (input: {InputTokens}, output: {OutputTokens}), " +
                    "efficiency: {Efficiency:P1} (reservation {ReservationId})",
                    chunkCount,
                    actualTotal,
                    totalInputTokens.Value,
                    totalOutputTokens.Value,
                    efficiency,
                    reservation.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Could not extract actual token usage from streaming response after {ChunkCount} chunks (reservation {ReservationId})",
                    chunkCount,
                    reservation.Id);
            }
        }
        finally
        {
            if (reservation != null)
            {
                await reservation.DisposeAsync();
            }
        }
    }
}
