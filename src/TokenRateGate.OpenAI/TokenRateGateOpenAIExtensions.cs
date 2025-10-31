using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using TokenRateGate.Abstractions;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Internal extension methods for integrating OpenAI ChatClient with TokenRateGate.
/// These methods are used internally by the RateLimitedChatClient wrapper and provide
/// the core rate limiting logic for OpenAI chat completions.
/// </summary>
internal static class TokenRateGateOpenAIExtensions
{
    /// <summary>
    /// Executes an OpenAI chat completion with automatic rate limiting.
    /// This method handles the complete flow: token estimation → reservation → API call → usage tracking.
    /// </summary>
    internal static async Task<ChatCompletion> ExecuteChatAsync(
        this ITokenRateGate rateGate,
        ChatClient client,
        IEnumerable<ChatMessage> messages,
        OpenAIChatHelper helper,
        ChatCompletionOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rateGate);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(helper);

        var log = logger ?? NullLogger.Instance;

        log.LogDebug(
            "Starting rate-limited chat completion for model {ModelName}",
            helper.ModelName);

        // Step 1: Estimate tokens using tiktoken
        var inputTokens = await helper.EstimateInputTokensAsync(messages, cancellationToken);
        var outputTokens = await helper.EstimateOutputTokensAsync(messages, cancellationToken);

        log.LogDebug(
            "Estimated tokens for {ModelName}: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
            helper.ModelName,
            inputTokens,
            outputTokens,
            inputTokens + outputTokens);

        // Step 2: Reserve capacity (blocks if needed)
        await using var reservation = await rateGate.ReserveTokensAsync(
            inputTokens,
            outputTokens,
            cancellationToken);

        log.LogDebug(
            "Reserved {ReservedTokens} tokens (reservation {ReservationId})",
            reservation.ReservedTokens,
            reservation.Id);

        ChatCompletion response;
        try
        {
            // Step 3: Execute the OpenAI API call
            response = await client.CompleteChatAsync(messages, options, cancellationToken);

            log.LogDebug(
                "Chat completion successful (completion {CompletionId})",
                response.Id);
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Chat completion failed for model {ModelName} (reservation {ReservationId})",
                helper.ModelName,
                reservation.Id);
            throw;
        }

        // Step 4: Extract actual usage from response
        var (actualInput, actualOutput) = helper.ExtractUsage(response);

        if (actualInput.HasValue && actualOutput.HasValue)
        {
            var actualTotal = actualInput.Value + actualOutput.Value;
            var reservedTotal = reservation.ReservedTokens;
            var efficiency = actualTotal > 0 ? (double)actualTotal / reservedTotal * 100.0 : 0;

            // Step 5: Record actual usage
            reservation.RecordActualUsage(actualInput.Value, actualOutput.Value);

            log.LogDebug(
                "Recorded actual usage for {ModelName}: input={ActualInput}, output={ActualOutput}, total={ActualTotal} " +
                "(reserved={Reserved}, efficiency={Efficiency:F1}%)",
                helper.ModelName,
                actualInput.Value,
                actualOutput.Value,
                actualTotal,
                reservedTotal,
                efficiency);
        }
        else
        {
            log.LogWarning(
                "Could not extract token usage from response for model {ModelName} (completion {CompletionId})",
                helper.ModelName,
                response.Id);
        }

        return response;
    }

    /// <summary>
    /// Executes an OpenAI chat completion with streaming and automatic rate limiting.
    /// This method streams response chunks while handling token reservation and usage tracking.
    /// </summary>
    internal static async IAsyncEnumerable<StreamingChatCompletionUpdate> ExecuteChatStreamingAsync(
        this ITokenRateGate rateGate,
        ChatClient client,
        IEnumerable<ChatMessage> messages,
        OpenAIChatHelper helper,
        ChatCompletionOptions? options = null,
        ILogger? logger = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rateGate);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(helper);

        var log = logger ?? NullLogger.Instance;

        log.LogDebug(
            "Starting rate-limited streaming chat completion for model {ModelName}",
            helper.ModelName);

        // Step 1: Estimate tokens
        var inputTokens = await helper.EstimateInputTokensAsync(messages, cancellationToken);
        var outputTokens = await helper.EstimateOutputTokensAsync(messages, cancellationToken);

        log.LogDebug(
            "Estimated tokens for streaming {ModelName}: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
            helper.ModelName,
            inputTokens,
            outputTokens,
            inputTokens + outputTokens);

        // Step 2: Reserve capacity for the entire stream duration
        await using var reservation = await rateGate.ReserveTokensAsync(
            inputTokens,
            outputTokens,
            cancellationToken);

        log.LogDebug(
            "Reserved {ReservedTokens} tokens for streaming (reservation {ReservationId})",
            reservation.ReservedTokens,
            reservation.Id);

        // Track usage across all chunks
        int? totalInputTokens = null;
        int? totalOutputTokens = null;
        var chunkCount = 0;

        // Step 3: Stream response chunks
        await foreach (var chunk in client.CompleteChatStreamingAsync(messages, options, cancellationToken)
            .ConfigureAwait(false)
            .WithCancellation(cancellationToken))
        {
            chunkCount++;

            // Step 4: Try to extract usage from each chunk (typically only in final chunk)
            var (inputFromChunk, outputFromChunk) = helper.ExtractStreamingUsage(chunk);

            if (inputFromChunk.HasValue)
                totalInputTokens = inputFromChunk.Value;

            if (outputFromChunk.HasValue)
                totalOutputTokens = outputFromChunk.Value;

            yield return chunk;
        }

        // Step 5: Record actual usage after stream completes
        if (totalInputTokens.HasValue && totalOutputTokens.HasValue)
        {
            var actualTotal = totalInputTokens.Value + totalOutputTokens.Value;
            var reservedTotal = reservation.ReservedTokens;
            var efficiency = actualTotal > 0 ? (double)actualTotal / reservedTotal * 100.0 : 0;

            reservation.RecordActualUsage(totalInputTokens.Value, totalOutputTokens.Value);

            log.LogDebug(
                "Streaming completed for {ModelName}: {ChunkCount} chunks, input={ActualInput}, output={ActualOutput}, total={ActualTotal} " +
                "(reserved={Reserved}, efficiency={Efficiency:F1}%)",
                helper.ModelName,
                chunkCount,
                totalInputTokens.Value,
                totalOutputTokens.Value,
                actualTotal,
                reservedTotal,
                efficiency);
        }
        else
        {
            log.LogWarning(
                "Could not extract token usage from streaming response for model {ModelName} after {ChunkCount} chunks",
                helper.ModelName,
                chunkCount);
        }
    }
}
