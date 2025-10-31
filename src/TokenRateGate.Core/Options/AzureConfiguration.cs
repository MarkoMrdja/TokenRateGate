namespace TokenRateGate.Core.Options;

/// <summary>
/// Azure OpenAI-specific configuration for TokenRateGate integration.
/// Used for both single-tenant and multi-tenant scenarios.
/// </summary>
public class AzureConfiguration
{
    /// <summary>
    /// Azure OpenAI endpoint URL.
    /// Example: "https://myresource.openai.azure.com"
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Optional API key for Azure OpenAI.
    /// If not specified, the ChatClient should be configured with credentials separately.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Mapping of deployment names to underlying model names for token estimation.
    /// Key: Deployment name (e.g., "gpt-4-deployment")
    /// Value: Model name (e.g., "gpt-4")
    /// </summary>
    public Dictionary<string, string> DeploymentToModelMap { get; set; } = new();

    /// <summary>
    /// Default model name to use when a deployment is not found in the map.
    /// Used as a fallback for token estimation.
    /// </summary>
    public string? DefaultModelName { get; set; }
}
