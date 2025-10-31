namespace TokenRateGate.Core.Options;

/// <summary>
/// OpenAI-specific configuration for TokenRateGate integration.
/// Used for both single-tenant and multi-tenant scenarios.
/// </summary>
public class OpenAIConfiguration
{
    /// <summary>
    /// The OpenAI model name to use for token estimation.
    /// Examples: "gpt-4", "gpt-4o", "gpt-3.5-turbo", "gpt-4-turbo"
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// Optional API key for OpenAI.
    /// If not specified, the ChatClient should be configured with credentials separately.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional organization ID for OpenAI API requests.
    /// </summary>
    public string? OrganizationId { get; set; }
}
