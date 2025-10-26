using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using OpenAI.Chat;
using TokenRateGate.Abstractions;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.OpenAI;

/// <summary>
/// Token estimator for OpenAI chat completion requests using tiktoken encodings.
/// Provides accurate token counting for GPT models by using Microsoft.ML.Tokenizers.
/// </summary>
public class OpenAITokenEstimator : ITokenEstimator<IEnumerable<ChatMessage>>
{
    private readonly TokenRateGateOptions _options;
    private readonly ILogger<OpenAITokenEstimator> _logger;
    private readonly string _modelName;
    private readonly OpenAIModelDefinitions.EncodingType _encodingType;
    private readonly Lazy<Tokenizer> _tokenizer;

    /// <summary>
    /// Creates a new OpenAI token estimator for the specified model.
    /// </summary>
    /// <param name="modelName">The OpenAI model name (e.g., "gpt-4", "gpt-3.5-turbo")</param>
    /// <param name="options">TokenRateGate configuration options for output estimation</param>
    /// <param name="logger">Logger for diagnostics</param>
    public OpenAITokenEstimator(
        string modelName,
        IOptions<TokenRateGateOptions> options,
        ILogger<OpenAITokenEstimator> logger)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _modelName = modelName;
        _options = options.Value;
        _logger = logger;
        _encodingType = OpenAIModelDefinitions.GetEncodingForModel(modelName);

        _tokenizer = new Lazy<Tokenizer>(() => CreateTokenizer(_encodingType));

        _logger.LogInformation(
            "Created OpenAI token estimator for model {ModelName} using {EncodingType} encoding",
            _modelName,
            _encodingType);
    }

    /// <summary>
    /// Creates a new OpenAI token estimator with explicit encoding type.
    /// </summary>
    /// <param name="modelName">The OpenAI model name</param>
    /// <param name="encodingType">The explicit encoding type to use</param>
    /// <param name="options">TokenRateGate configuration options</param>
    /// <param name="logger">Logger for diagnostics</param>
    public OpenAITokenEstimator(
        string modelName,
        OpenAIModelDefinitions.EncodingType encodingType,
        IOptions<TokenRateGateOptions> options,
        ILogger<OpenAITokenEstimator> logger)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _modelName = modelName;
        _encodingType = encodingType;
        _options = options.Value;
        _logger = logger;

        _tokenizer = new Lazy<Tokenizer>(() => CreateTokenizer(_encodingType));

        _logger.LogInformation(
            "Created OpenAI token estimator for model {ModelName} using explicit {EncodingType} encoding",
            _modelName,
            _encodingType);
    }

    /// <inheritdoc />
    public Task<int> EstimateInputTokensAsync(IEnumerable<ChatMessage> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var messages = request.ToList();
            if (messages.Count == 0)
            {
                return Task.FromResult(0);
            }

            var tokenizer = _tokenizer.Value;
            var totalTokens = 0;

            // Add base overhead for the request
            totalTokens += OpenAIModelDefinitions.GetReplyPrimingTokens(_encodingType);

            // Count tokens for each message
            foreach (var message in messages)
            {
                // Add per-message overhead
                totalTokens += OpenAIModelDefinitions.GetTokensPerMessage(_encodingType);

                // Count role tokens
                var role = GetRoleName(message);
                totalTokens += CountTokens(tokenizer, role);

                // Count content tokens
                foreach (var contentPart in message.Content)
                {
                    if (contentPart.Kind == ChatMessageContentPartKind.Text)
                    {
                        var textContent = contentPart.Text ?? string.Empty;
                        totalTokens += CountTokens(tokenizer, textContent);
                    }
                    else if (contentPart.Kind == ChatMessageContentPartKind.Image)
                    {
                        // Image tokens are complex and depend on image size and detail level
                        // For now, use a conservative estimate
                        // Base image token cost is around 85 tokens + tokens for the image tiles
                        // Default detail=auto typically results in 85-170 tokens per image
                        totalTokens += 170; // Conservative estimate for "high" detail
                        _logger.LogDebug("Added estimated 170 tokens for image content");
                    }
                    // Note: InputAudio is marked as experimental in OpenAI SDK
                    // Suppress warning for now as this is legitimate usage
#pragma warning disable OPENAI001
                    else if (contentPart.Kind == ChatMessageContentPartKind.InputAudio)
                    {
                        // Audio input tokens are calculated by the API
                        // Estimate ~50 tokens per second of audio (very rough approximation)
                        totalTokens += 500; // Conservative estimate
                        _logger.LogDebug("Added estimated 500 tokens for audio input");
                    }
#pragma warning restore OPENAI001
                }

                // Add tokens for function/tool calls if present (only on assistant messages)
                if (message is AssistantChatMessage assistantMessage && assistantMessage.ToolCalls?.Count > 0)
                {
                    foreach (var toolCall in assistantMessage.ToolCalls)
                    {
                        totalTokens += CountTokens(tokenizer, toolCall.FunctionName);
                        totalTokens += CountTokens(tokenizer, toolCall.FunctionArguments.ToString());
                        totalTokens += 3; // Overhead for function call structure
                    }
                }
            }

            _logger.LogDebug(
                "Estimated {TokenCount} input tokens for {MessageCount} messages using {EncodingType}",
                totalTokens,
                messages.Count,
                _encodingType);

            return Task.FromResult(totalTokens);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error estimating input tokens for model {ModelName}", _modelName);

            // Fallback to character-based estimation (very rough: ~4 characters per token)
            var messages = request.ToList();
            var charCount = messages.Sum(m => m.Content.Sum(c => c.Text?.Length ?? 0));
            var fallbackEstimate = Math.Max(1, charCount / 4);

            _logger.LogWarning(
                "Using fallback character-based estimation: {TokenCount} tokens from {CharCount} characters",
                fallbackEstimate,
                charCount);

            return Task.FromResult(fallbackEstimate);
        }
    }

    /// <inheritdoc />
    public Task<int> EstimateOutputTokensAsync(IEnumerable<ChatMessage> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Output token estimation is inherently uncertain
        // Use the configured strategy from TokenRateGateOptions
        var inputTokens = EstimateInputTokensAsync(request, cancellationToken).GetAwaiter().GetResult();

        var outputTokens = _options.OutputEstimationStrategy switch
        {
            OutputEstimationStrategy.FixedMultiplier => (int)(inputTokens * _options.OutputMultiplier),
            OutputEstimationStrategy.FixedAmount => _options.DefaultOutputTokens,
            OutputEstimationStrategy.Conservative => inputTokens, // Assume output = input (2x total)
            _ => _options.DefaultOutputTokens
        };

        _logger.LogDebug(
            "Estimated {OutputTokens} output tokens using {Strategy} strategy (input: {InputTokens})",
            outputTokens,
            _options.OutputEstimationStrategy,
            inputTokens);

        return Task.FromResult(outputTokens);
    }

    /// <summary>
    /// Creates a tokenizer for the specified encoding type.
    /// </summary>
    private static Tokenizer CreateTokenizer(OpenAIModelDefinitions.EncodingType encodingType)
    {
        return encodingType switch
        {
            OpenAIModelDefinitions.EncodingType.Cl100kBase => TiktokenTokenizer.CreateForModel("gpt-4"),
            OpenAIModelDefinitions.EncodingType.O200kBase => TiktokenTokenizer.CreateForModel("gpt-4o"),
            OpenAIModelDefinitions.EncodingType.P50kBase => TiktokenTokenizer.CreateForEncoding("p50k_base"),
            OpenAIModelDefinitions.EncodingType.R50kBase => TiktokenTokenizer.CreateForEncoding("r50k_base"),
            _ => throw new ArgumentException($"Unsupported encoding type: {encodingType}")
        };
    }

    /// <summary>
    /// Counts tokens in a text string using the tokenizer.
    /// </summary>
    private int CountTokens(Tokenizer tokenizer, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var encoded = tokenizer.CountTokens(text);
            return encoded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error counting tokens for text of length {Length}, using fallback", text.Length);
            // Fallback: ~4 characters per token
            return Math.Max(1, text.Length / 4);
        }
    }

    /// <summary>
    /// Gets the role name from a chat message for token counting.
    /// </summary>
    private static string GetRoleName(ChatMessage message)
    {
        return message switch
        {
            SystemChatMessage => "system",
            UserChatMessage => "user",
            AssistantChatMessage => "assistant",
            ToolChatMessage => "tool",
            FunctionChatMessage => "function",
            _ => "user" // Default fallback
        };
    }
}
