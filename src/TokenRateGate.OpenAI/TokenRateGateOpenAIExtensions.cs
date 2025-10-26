using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using TokenRateGate.Abstractions;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Extension methods for integrating OpenAI ChatClient with TokenRateGate.
/// These methods provide a clean API for rate-limited OpenAI chat completions
/// with automatic token estimation, reservation, and usage tracking.
/// </summary>
public static class TokenRateGateOpenAIExtensions
{
    /// <summary>
    /// Executes an OpenAI chat completion with automatic rate limiting.
    /// This method handles the complete flow: token estimation → reservation → API call → usage tracking.
    /// </summary>
    /// <param name="rateGate">The token rate gate instance</param>
    /// <param name="client">The OpenAI ChatClient to use for the API call</param>
    /// <param name="messages">The chat messages to send</param>
    /// <param name="helper">The OpenAI chat helper configured for the specific model</param>
    /// <param name="options">Optional chat completion options (temperature, max_tokens, etc.)</param>
    /// <param name="logger">Optional logger for detailed operation tracking</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The chat completion response from OpenAI</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled or times out waiting for capacity</exception>
    /// <remarks>
    /// This method performs the following steps:
    /// 1. Estimates input and output tokens using tiktoken
    /// 2. Reserves capacity in the rate limiter (blocks if capacity unavailable)
    /// 3. Executes the chat completion via the provided client
    /// 4. Extracts actual token usage from the response
    /// 5. Records actual usage and releases the reservation
    ///
    /// Example usage:
    /// <code>
    /// var rateGate = serviceProvider.GetRequiredService&lt;ITokenRateGate&gt;();
    /// var client = new ChatClient("gpt-4", apiKey);
    /// var helper = new OpenAIChatHelper("gpt-4");
    /// var messages = new List&lt;ChatMessage&gt; { new UserChatMessage("Hello!") };
    ///
    /// var response = await rateGate.ExecuteChatAsync(client, messages, helper);
    /// Console.WriteLine(response.Content[0].Text);
    /// </code>
    /// </remarks>
    public static async Task<ChatCompletion> ExecuteChatAsync(
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
    /// <param name="rateGate">The token rate gate instance</param>
    /// <param name="client">The OpenAI ChatClient to use for the API call</param>
    /// <param name="messages">The chat messages to send</param>
    /// <param name="helper">The OpenAI chat helper configured for the specific model</param>
    /// <param name="options">Optional chat completion options (temperature, max_tokens, etc.)</param>
    /// <param name="logger">Optional logger for detailed operation tracking</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An async enumerable of streaming chat completion updates</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled or times out waiting for capacity</exception>
    /// <remarks>
    /// This method performs the following steps:
    /// 1. Estimates input and output tokens using tiktoken
    /// 2. Reserves capacity in the rate limiter (blocks if capacity unavailable)
    /// 3. Streams chat completion chunks via the provided client
    /// 4. Extracts actual token usage from the final chunk
    /// 5. Records actual usage and releases the reservation after streaming completes
    ///
    /// The reservation is held for the entire duration of streaming and is automatically
    /// released when the stream completes, even if an error occurs or the operation is cancelled.
    ///
    /// Example usage:
    /// <code>
    /// var rateGate = serviceProvider.GetRequiredService&lt;ITokenRateGate&gt;();
    /// var client = new ChatClient("gpt-4", apiKey);
    /// var helper = new OpenAIChatHelper("gpt-4");
    /// var messages = new List&lt;ChatMessage&gt; { new UserChatMessage("Hello!") };
    ///
    /// await foreach (var update in rateGate.ExecuteChatStreamingAsync(client, messages, helper))
    /// {
    ///     if (update.ContentUpdate.Count &gt; 0)
    ///         Console.Write(update.ContentUpdate[0].Text);
    /// }
    /// </code>
    /// </remarks>
    public static async IAsyncEnumerable<StreamingChatCompletionUpdate> ExecuteChatStreamingAsync(
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
