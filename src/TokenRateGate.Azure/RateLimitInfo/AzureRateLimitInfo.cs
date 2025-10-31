namespace TokenRateGate.Azure.RateLimitInfo;

/// <summary>
/// Represents rate limit information extracted from Azure OpenAI response headers.
/// Azure OpenAI returns rate limit information in response headers that can be used
/// to dynamically adjust rate limiting behavior or monitor current usage.
/// </summary>
public class AzureRateLimitInfo
{
    /// <summary>
    /// The maximum number of requests allowed per time period.
    /// Header: x-ratelimit-limit-requests
    /// </summary>
    public int? RequestLimit { get; init; }

    /// <summary>
    /// The maximum number of tokens allowed per time period.
    /// Header: x-ratelimit-limit-tokens
    /// </summary>
    public int? TokenLimit { get; init; }

    /// <summary>
    /// The number of requests remaining in the current time period.
    /// Header: x-ratelimit-remaining-requests
    /// </summary>
    public int? RemainingRequests { get; init; }

    /// <summary>
    /// The number of tokens remaining in the current time period.
    /// Header: x-ratelimit-remaining-tokens
    /// </summary>
    public int? RemainingTokens { get; init; }

    /// <summary>
    /// Time when the request rate limit will reset (Unix timestamp in seconds).
    /// Header: x-ratelimit-reset-requests
    /// </summary>
    public DateTimeOffset? RequestsResetAt { get; init; }

    /// <summary>
    /// Time when the token rate limit will reset (Unix timestamp in seconds).
    /// Header: x-ratelimit-reset-tokens
    /// </summary>
    public DateTimeOffset? TokensResetAt { get; init; }

    /// <summary>
    /// Number of seconds to wait before retrying when rate limited (429 response).
    /// Header: retry-after
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// Indicates whether the response included rate limit information.
    /// </summary>
    public bool HasRateLimitInfo => RequestLimit.HasValue || TokenLimit.HasValue;

    /// <summary>
    /// Calculates the percentage of request capacity remaining.
    /// Returns null if the information is not available.
    /// </summary>
    public double? RequestCapacityPercentage
    {
        get
        {
            if (!RequestLimit.HasValue || !RemainingRequests.HasValue || RequestLimit.Value == 0)
                return null;

            return (double)RemainingRequests.Value / RequestLimit.Value * 100.0;
        }
    }

    /// <summary>
    /// Calculates the percentage of token capacity remaining.
    /// Returns null if the information is not available.
    /// </summary>
    public double? TokenCapacityPercentage
    {
        get
        {
            if (!TokenLimit.HasValue || !RemainingTokens.HasValue || TokenLimit.Value == 0)
                return null;

            return (double)RemainingTokens.Value / TokenLimit.Value * 100.0;
        }
    }

    /// <summary>
    /// Indicates whether the request capacity is running low (below 20%).
    /// </summary>
    public bool IsRequestCapacityLow => RequestCapacityPercentage < 20.0;

    /// <summary>
    /// Indicates whether the token capacity is running low (below 20%).
    /// </summary>
    public bool IsTokenCapacityLow => TokenCapacityPercentage < 20.0;

    /// <summary>
    /// Returns a string representation of the rate limit information.
    /// </summary>
    public override string ToString()
    {
        if (!HasRateLimitInfo)
            return "No rate limit information available";

        var parts = new List<string>();

        if (TokenLimit.HasValue && RemainingTokens.HasValue)
            parts.Add($"Tokens: {RemainingTokens}/{TokenLimit} ({TokenCapacityPercentage:F1}%)");

        if (RequestLimit.HasValue && RemainingRequests.HasValue)
            parts.Add($"Requests: {RemainingRequests}/{RequestLimit} ({RequestCapacityPercentage:F1}%)");

        if (TokensResetAt.HasValue)
        {
            var timeUntilReset = TokensResetAt.Value - DateTimeOffset.UtcNow;
            if (timeUntilReset.TotalSeconds > 0)
                parts.Add($"Token reset in {timeUntilReset.TotalSeconds:F0}s");
        }

        if (RetryAfterSeconds.HasValue)
            parts.Add($"Retry after {RetryAfterSeconds}s");

        return string.Join(", ", parts);
    }
}
