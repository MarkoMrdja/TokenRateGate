namespace TokenRateGate.Core.Options;

/// <summary>
/// Root configuration for TokenRateGate with support for multi-tenant scenarios.
/// Can be bound from appsettings.json or configured programmatically.
/// </summary>
public class TokenRateGateConfiguration
{
    /// <summary>
    /// Configuration section name for binding from appsettings.json
    /// </summary>
    public const string SectionName = "TokenRateGate";

    /// <summary>
    /// Default (single-tenant) configuration.
    /// Used when no specific tenant is specified.
    /// </summary>
    public TokenRateGateOptions? Default { get; set; }

    /// <summary>
    /// Tenant-specific configurations for multi-tenant scenarios.
    /// Key is the tenant identifier, value is the tenant configuration.
    /// </summary>
    public Dictionary<string, TenantConfiguration> Tenants { get; set; } = new();
}
