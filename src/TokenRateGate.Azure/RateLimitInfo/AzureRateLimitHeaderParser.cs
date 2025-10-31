using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TokenRateGate.Azure.RateLimitInfo;

/// <summary>
/// Parses Azure OpenAI rate limit information from HTTP response headers.
/// Azure OpenAI returns rate limiting information in several headers that can be used
/// to monitor usage and adjust rate limiting behavior dynamically.
/// </summary>
public class AzureRateLimitHeaderParser
{
    private readonly ILogger<AzureRateLimitHeaderParser> _logger;

    /// <summary>
    /// Creates a new Azure rate limit header parser.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics</param>
    public AzureRateLimitHeaderParser(ILogger<AzureRateLimitHeaderParser>? logger = null)
    {
        _logger = logger ?? NullLogger<AzureRateLimitHeaderParser>.Instance;
    }

    /// <summary>
    /// Parses rate limit information from response headers.
    /// </summary>
    /// <param name="headers">The HTTP response headers</param>
    /// <returns>Parsed rate limit information, or an empty object if headers are not present</returns>
    public AzureRateLimitInfo ParseHeaders(IDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var info = new AzureRateLimitInfo
        {
            RequestLimit = ParseIntHeader(headers, "x-ratelimit-limit-requests"),
            TokenLimit = ParseIntHeader(headers, "x-ratelimit-limit-tokens"),
            RemainingRequests = ParseIntHeader(headers, "x-ratelimit-remaining-requests"),
            RemainingTokens = ParseIntHeader(headers, "x-ratelimit-remaining-tokens"),
            RequestsResetAt = ParseUnixTimestampHeader(headers, "x-ratelimit-reset-requests"),
            TokensResetAt = ParseUnixTimestampHeader(headers, "x-ratelimit-reset-tokens"),
            RetryAfterSeconds = ParseIntHeader(headers, "retry-after")
        };

        if (info.HasRateLimitInfo)
        {
            _logger.LogDebug(
                "Parsed Azure rate limit info: {RateLimitInfo}",
                info.ToString());

            if (info.IsTokenCapacityLow)
            {
                _logger.LogWarning(
                    "Token capacity is low: {RemainingTokens}/{TokenLimit} ({Percentage:F1}%)",
                    info.RemainingTokens,
                    info.TokenLimit,
                    info.TokenCapacityPercentage);
            }

            if (info.IsRequestCapacityLow)
            {
                _logger.LogWarning(
                    "Request capacity is low: {RemainingRequests}/{RequestLimit} ({Percentage:F1}%)",
                    info.RemainingRequests,
                    info.RequestLimit,
                    info.RequestCapacityPercentage);
            }
        }
        else
        {
            _logger.LogDebug("No rate limit information found in response headers");
        }

        return info;
    }

    /// <summary>
    /// Parses an integer value from a header.
    /// </summary>
    private int? ParseIntHeader(IDictionary<string, string> headers, string headerName)
    {
        if (!headers.TryGetValue(headerName, out var value))
            return null;

        if (int.TryParse(value, out var result))
            return result;

        _logger.LogWarning(
            "Failed to parse integer header {HeaderName}: {Value}",
            headerName,
            value);

        return null;
    }

    /// <summary>
    /// Parses a Unix timestamp (seconds since epoch) from a header.
    /// </summary>
    private DateTimeOffset? ParseUnixTimestampHeader(IDictionary<string, string> headers, string headerName)
    {
        if (!headers.TryGetValue(headerName, out var value))
            return null;

        if (long.TryParse(value, out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid Unix timestamp in header {HeaderName}: {Value}",
                    headerName,
                    value);
                return null;
            }
        }

        _logger.LogWarning(
            "Failed to parse Unix timestamp header {HeaderName}: {Value}",
            headerName,
            value);

        return null;
    }
}
