namespace TokenRateGate.OpenAI;

/// <summary>
/// Defines OpenAI model metadata including encoding types for token estimation.
/// </summary>
public static class OpenAIModelDefinitions
{
    /// <summary>
    /// Encoding types used by different OpenAI model families.
    /// </summary>
    public enum EncodingType
    {
        /// <summary>
        /// cl100k_base encoding used by GPT-4, GPT-3.5-turbo, and text-embedding-ada-002
        /// </summary>
        Cl100kBase,

        /// <summary>
        /// o200k_base encoding used by GPT-4o and newer models
        /// </summary>
        O200kBase,

        /// <summary>
        /// p50k_base encoding used by older models like text-davinci-002 and code-davinci-002
        /// </summary>
        P50kBase,

        /// <summary>
        /// r50k_base (gpt2) encoding used by GPT-3 models
        /// </summary>
        R50kBase
    }

    /// <summary>
    /// Metadata about an OpenAI model including its encoding type.
    /// </summary>
    public record ModelInfo(string Name, EncodingType Encoding, string Description);

    /// <summary>
    /// Well-known OpenAI models with their encoding types.
    /// </summary>
    public static class Models
    {
        // GPT-4o family (o200k_base)
        public static readonly ModelInfo Gpt4o = new("gpt-4o", EncodingType.O200kBase, "GPT-4o with 128k context");
        public static readonly ModelInfo Gpt4oMini = new("gpt-4o-mini", EncodingType.O200kBase, "Affordable GPT-4o variant");
        public static readonly ModelInfo Gpt4oAudioPreview = new("gpt-4o-audio-preview", EncodingType.O200kBase, "GPT-4o with audio capabilities");
        public static readonly ModelInfo Gpt4o_20250115 = new("gpt-4o-2025-01-15", EncodingType.O200kBase, "GPT-4o snapshot from January 2025");
        public static readonly ModelInfo Gpt4oMini_20250115 = new("gpt-4o-mini-2025-01-15", EncodingType.O200kBase, "GPT-4o-mini snapshot from January 2025");

        // GPT-4 family (cl100k_base)
        public static readonly ModelInfo Gpt4 = new("gpt-4", EncodingType.Cl100kBase, "GPT-4 with 8k context");
        public static readonly ModelInfo Gpt4_32k = new("gpt-4-32k", EncodingType.Cl100kBase, "GPT-4 with 32k context");
        public static readonly ModelInfo Gpt4Turbo = new("gpt-4-turbo", EncodingType.Cl100kBase, "GPT-4 Turbo with 128k context");
        public static readonly ModelInfo Gpt4TurboPreview = new("gpt-4-turbo-preview", EncodingType.Cl100kBase, "GPT-4 Turbo Preview");
        public static readonly ModelInfo Gpt4_1106Preview = new("gpt-4-1106-preview", EncodingType.Cl100kBase, "GPT-4 Turbo from November 2023");
        public static readonly ModelInfo Gpt4_0125Preview = new("gpt-4-0125-preview", EncodingType.Cl100kBase, "GPT-4 Turbo from January 2024");
        public static readonly ModelInfo Gpt4VisionPreview = new("gpt-4-vision-preview", EncodingType.Cl100kBase, "GPT-4 with vision capabilities");

        // GPT-3.5 family (cl100k_base)
        public static readonly ModelInfo Gpt35Turbo = new("gpt-3.5-turbo", EncodingType.Cl100kBase, "GPT-3.5 Turbo with 16k context");
        public static readonly ModelInfo Gpt35Turbo16k = new("gpt-3.5-turbo-16k", EncodingType.Cl100kBase, "GPT-3.5 Turbo with 16k context");
        public static readonly ModelInfo Gpt35Turbo_1106 = new("gpt-3.5-turbo-1106", EncodingType.Cl100kBase, "GPT-3.5 Turbo from November 2023");
        public static readonly ModelInfo Gpt35Turbo_0125 = new("gpt-3.5-turbo-0125", EncodingType.Cl100kBase, "GPT-3.5 Turbo from January 2024");

        // O3 family (o200k_base) - reasoning models
        public static readonly ModelInfo O3Mini = new("o3-mini", EncodingType.O200kBase, "O3-mini reasoning model");

        // Embeddings (cl100k_base)
        public static readonly ModelInfo TextEmbeddingAda002 = new("text-embedding-ada-002", EncodingType.Cl100kBase, "Ada v2 embeddings");
        public static readonly ModelInfo TextEmbedding3Small = new("text-embedding-3-small", EncodingType.Cl100kBase, "Embedding v3 small");
        public static readonly ModelInfo TextEmbedding3Large = new("text-embedding-3-large", EncodingType.Cl100kBase, "Embedding v3 large");
    }

    private static readonly Dictionary<string, EncodingType> _modelEncodingMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // GPT-4o family (o200k_base)
        ["gpt-4o"] = EncodingType.O200kBase,
        ["gpt-4o-mini"] = EncodingType.O200kBase,
        ["gpt-4o-audio-preview"] = EncodingType.O200kBase,
        ["gpt-4o-2025-01-15"] = EncodingType.O200kBase,
        ["gpt-4o-mini-2025-01-15"] = EncodingType.O200kBase,
        ["o3-mini"] = EncodingType.O200kBase,

        // GPT-4 family (cl100k_base)
        ["gpt-4"] = EncodingType.Cl100kBase,
        ["gpt-4-32k"] = EncodingType.Cl100kBase,
        ["gpt-4-turbo"] = EncodingType.Cl100kBase,
        ["gpt-4-turbo-preview"] = EncodingType.Cl100kBase,
        ["gpt-4-1106-preview"] = EncodingType.Cl100kBase,
        ["gpt-4-0125-preview"] = EncodingType.Cl100kBase,
        ["gpt-4-vision-preview"] = EncodingType.Cl100kBase,
        ["gpt-4-0613"] = EncodingType.Cl100kBase,
        ["gpt-4-32k-0613"] = EncodingType.Cl100kBase,

        // GPT-3.5 family (cl100k_base)
        ["gpt-3.5-turbo"] = EncodingType.Cl100kBase,
        ["gpt-3.5-turbo-16k"] = EncodingType.Cl100kBase,
        ["gpt-3.5-turbo-1106"] = EncodingType.Cl100kBase,
        ["gpt-3.5-turbo-0125"] = EncodingType.Cl100kBase,
        ["gpt-3.5-turbo-0613"] = EncodingType.Cl100kBase,
        ["gpt-3.5-turbo-16k-0613"] = EncodingType.Cl100kBase,

        // Embeddings (cl100k_base)
        ["text-embedding-ada-002"] = EncodingType.Cl100kBase,
        ["text-embedding-3-small"] = EncodingType.Cl100kBase,
        ["text-embedding-3-large"] = EncodingType.Cl100kBase,

        // Older models (p50k_base)
        ["text-davinci-002"] = EncodingType.P50kBase,
        ["text-davinci-003"] = EncodingType.P50kBase,
        ["code-davinci-002"] = EncodingType.P50kBase,

        // GPT-3 models (r50k_base)
        ["text-curie-001"] = EncodingType.R50kBase,
        ["text-babbage-001"] = EncodingType.R50kBase,
        ["text-ada-001"] = EncodingType.R50kBase,
        ["davinci"] = EncodingType.R50kBase,
        ["curie"] = EncodingType.R50kBase,
        ["babbage"] = EncodingType.R50kBase,
        ["ada"] = EncodingType.R50kBase,
    };

    /// <summary>
    /// Gets the encoding type for a given model name.
    /// </summary>
    /// <param name="modelName">The OpenAI model name (case-insensitive)</param>
    /// <returns>The encoding type for the model</returns>
    /// <exception cref="ArgumentException">Thrown when the model name is unknown</exception>
    public static EncodingType GetEncodingForModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name cannot be null or whitespace", nameof(modelName));
        }

        // Try exact match first
        if (_modelEncodingMap.TryGetValue(modelName, out var encoding))
        {
            return encoding;
        }

        // Try prefix match for versioned models (e.g., "gpt-4o-2024-08-06" should match "gpt-4o")
        foreach (var kvp in _modelEncodingMap)
        {
            if (modelName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Default to cl100k_base for unknown GPT models (most common encoding)
        if (modelName.Contains("gpt", StringComparison.OrdinalIgnoreCase))
        {
            return EncodingType.Cl100kBase;
        }

        throw new ArgumentException(
            $"Unknown model '{modelName}'. Please specify the encoding type explicitly or update the model definitions.",
            nameof(modelName));
    }

    /// <summary>
    /// Tries to get the encoding type for a model name.
    /// </summary>
    /// <param name="modelName">The OpenAI model name (case-insensitive)</param>
    /// <param name="encoding">The encoding type if found</param>
    /// <returns>True if the encoding was found, false otherwise</returns>
    public static bool TryGetEncodingForModel(string modelName, out EncodingType encoding)
    {
        try
        {
            encoding = GetEncodingForModel(modelName);
            return true;
        }
        catch (ArgumentException)
        {
            encoding = default;
            return false;
        }
    }

    /// <summary>
    /// Estimates tokens per message for formatting overhead in chat completions.
    /// OpenAI chat format adds tokens for message structure (role, delimiters, etc.)
    /// </summary>
    /// <param name="encodingType">The encoding type being used</param>
    /// <returns>Estimated tokens per message for formatting overhead</returns>
    public static int GetTokensPerMessage(EncodingType encodingType)
    {
        return encodingType switch
        {
            // cl100k_base models (GPT-4, GPT-3.5-turbo)
            // Each message has: <|start|>{role/name}\n{content}<|end|>\n
            EncodingType.Cl100kBase => 3,

            // o200k_base models (GPT-4o) - similar overhead
            EncodingType.O200kBase => 3,

            // Older encodings have slightly different overhead
            EncodingType.P50kBase => 3,
            EncodingType.R50kBase => 3,

            _ => 3
        };
    }

    /// <summary>
    /// Estimates tokens per name field in messages.
    /// When a message has a 'name' field, it adds extra tokens.
    /// </summary>
    /// <param name="encodingType">The encoding type being used</param>
    /// <returns>Estimated tokens per name field</returns>
    public static int GetTokensPerName(EncodingType encodingType)
    {
        return encodingType switch
        {
            EncodingType.Cl100kBase => 1,
            EncodingType.O200kBase => 1,
            EncodingType.P50kBase => -1, // One less token for older models
            EncodingType.R50kBase => -1,
            _ => 1
        };
    }

    /// <summary>
    /// Estimates the base overhead tokens for a chat completion request.
    /// This accounts for the priming/reply tokens that OpenAI adds.
    /// </summary>
    /// <param name="encodingType">The encoding type being used</param>
    /// <returns>Estimated base overhead tokens</returns>
    public static int GetReplyPrimingTokens(EncodingType encodingType)
    {
        return encodingType switch
        {
            EncodingType.Cl100kBase => 3, // Priming for assistant reply
            EncodingType.O200kBase => 3,
            EncodingType.P50kBase => 3,
            EncodingType.R50kBase => 3,
            _ => 3
        };
    }
}
