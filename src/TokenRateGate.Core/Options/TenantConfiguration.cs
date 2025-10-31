namespace TokenRateGate.Core.Options;

/// <summary>
/// Configuration for a single tenant in a multi-tenant TokenRateGate setup.
/// </summary>
public class TenantConfiguration
{
    /// <summary>
    /// Token rate limiting options for this tenant
    /// </summary>
    public TokenRateGateOptions? RateLimit { get; set; }

    /// <summary>
    /// Optional OpenAI-specific configuration for this tenant
    /// </summary>
    public OpenAIConfiguration? OpenAI { get; set; }

    /// <summary>
    /// Optional Azure OpenAI-specific configuration for this tenant
    /// </summary>
    public AzureConfiguration? Azure { get; set; }
}
