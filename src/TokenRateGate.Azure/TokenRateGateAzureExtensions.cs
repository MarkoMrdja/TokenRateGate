using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using TokenRateGate.Abstractions;

namespace TokenRateGate.Azure;

/// <summary>
/// Internal extension methods for integrating Azure OpenAI ChatClient with TokenRateGate.
/// These methods are used internally by the RateLimitedAzureChatClient wrapper and provide
/// the core rate limiting logic for Azure OpenAI chat completions.
/// </summary>
internal static class TokenRateGateAzureExtensions
{
    /// <summary>
    /// Executes an Azure OpenAI chat completion with automatic rate limiting.
    /// This method handles the complete flow: token estimation → reservation → API call → usage tracking.
    /// </summary>
    internal static async Task<ChatCompletion> ExecuteAzureChatAsync(
        this ITokenRateGate rateGate,
        ChatClient client,
        IEnumerable<ChatMessage> messages,
        AzureChatHelper helper,
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
            "Starting rate-limited Azure OpenAI chat completion for deployment {DeploymentName} (model {ModelName})",
            helper.DeploymentName,
            helper.ModelName);

        // Step 1: Estimate tokens using tiktoken
        var inputTokens = await helper.EstimateInputTokensAsync(messages, cancellationToken);
        var outputTokens = await helper.EstimateOutputTokensAsync(messages, cancellationToken);

        log.LogDebug(
            "Estimated tokens for deployment {DeploymentName}: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
            helper.DeploymentName,
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
            // Step 3: Execute the Azure OpenAI API call
            response = await client.CompleteChatAsync(messages, options, cancellationToken);

            log.LogDebug(
                "Azure chat completion successful (completion {CompletionId})",
                response.Id);
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Azure chat completion failed for deployment {DeploymentName} (reservation {ReservationId})",
                helper.DeploymentName,
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
                "Recorded actual usage for deployment {DeploymentName}: input={ActualInput}, output={ActualOutput}, total={ActualTotal} " +
                "(reserved={Reserved}, efficiency={Efficiency:F1}%)",
                helper.DeploymentName,
                actualInput.Value,
                actualOutput.Value,
                actualTotal,
                reservedTotal,
                efficiency);
        }
        else
        {
            log.LogWarning(
                "Could not extract token usage from response for deployment {DeploymentName} (completion {CompletionId})",
                helper.DeploymentName,
                response.Id);
        }

        return response;
    }

    /// <summary>
    /// Executes an Azure OpenAI chat completion with streaming and automatic rate limiting.
    /// This method streams response chunks while handling token reservation and usage tracking.
    /// </summary>
    internal static async IAsyncEnumerable<StreamingChatCompletionUpdate> ExecuteAzureChatStreamingAsync(
        this ITokenRateGate rateGate,
        ChatClient client,
        IEnumerable<ChatMessage> messages,
        AzureChatHelper helper,
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
            "Starting rate-limited streaming Azure OpenAI chat completion for deployment {DeploymentName} (model {ModelName})",
            helper.DeploymentName,
            helper.ModelName);

        // Step 1: Estimate tokens
        var inputTokens = await helper.EstimateInputTokensAsync(messages, cancellationToken);
        var outputTokens = await helper.EstimateOutputTokensAsync(messages, cancellationToken);

        log.LogDebug(
            "Estimated tokens for streaming deployment {DeploymentName}: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
            helper.DeploymentName,
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
                "Streaming completed for deployment {DeploymentName}: {ChunkCount} chunks, input={ActualInput}, output={ActualOutput}, total={ActualTotal} " +
                "(reserved={Reserved}, efficiency={Efficiency:F1}%)",
                helper.DeploymentName,
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
                "Could not extract token usage from streaming response for deployment {DeploymentName} after {ChunkCount} chunks",
                helper.DeploymentName,
                chunkCount);
        }
    }
}
