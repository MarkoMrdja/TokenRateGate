namespace TokenRateGate.Abstractions;

/// <summary>
/// Factory interface for creating and managing multiple TokenRateGate instances.
/// This enables multi-tenant scenarios where different rate limits apply to
/// different models, users, or API endpoints.
/// </summary>
public interface ITokenRateGateFactory
{
    /// <summary>
    /// Gets or creates a TokenRateGate instance with the specified name.
    /// Multiple calls with the same name should return the same instance.
    /// </summary>
    /// <param name="name">
    /// The unique identifier for this rate gate instance.
    /// Typically corresponds to a model name, tenant ID, or API endpoint.
    /// </param>
    /// <returns>The TokenRateGate instance associated with the specified name</returns>
    /// <exception cref="ArgumentNullException">Thrown when name is null</exception>
    /// <exception cref="ArgumentException">Thrown when name is empty or whitespace</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the factory cannot create an instance (e.g., missing configuration)
    /// </exception>
    ITokenRateGate GetOrCreate(string name);

    /// <summary>
    /// Gets an existing TokenRateGate instance by name, or null if it doesn't exist.
    /// This method will not create a new instance.
    /// </summary>
    /// <param name="name">The unique identifier for the rate gate instance</param>
    /// <returns>
    /// The TokenRateGate instance if it exists, or null if no instance with that name has been created
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when name is null</exception>
    /// <exception cref="ArgumentException">Thrown when name is empty or whitespace</exception>
    ITokenRateGate? Get(string name);

    /// <summary>
    /// Checks whether a TokenRateGate instance with the specified name exists.
    /// </summary>
    /// <param name="name">The unique identifier to check</param>
    /// <returns>True if an instance with the specified name exists, false otherwise</returns>
    /// <exception cref="ArgumentNullException">Thrown when name is null</exception>
    /// <exception cref="ArgumentException">Thrown when name is empty or whitespace</exception>
    bool Exists(string name);

    /// <summary>
    /// Gets all active TokenRateGate instance names that have been created by this factory.
    /// </summary>
    /// <returns>An enumerable of all instance names</returns>
    IEnumerable<string> GetAllNames();
}

/// <summary>
/// Represents a token reservation that must be released when the request completes.
/// </summary>
public interface ITokenReservation : IAsyncDisposable
{
    /// <summary>
    /// Records the actual token usage after receiving the LLM response.
    /// This should be called before disposing the reservation to enable accurate tracking.
    /// </summary>
    /// <param name="actualInputTokens">The actual number of input tokens consumed</param>
    /// <param name="actualOutputTokens">The actual number of output tokens generated</param>
    void RecordActualUsage(int actualInputTokens, int actualOutputTokens);

    /// <summary>
    /// The unique identifier for this reservation.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// The number of tokens reserved for this request.
    /// </summary>
    int ReservedTokens { get; }

    /// <summary>
    /// The timestamp when this reservation was created.
    /// </summary>
    DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// Provides statistics about current token usage and capacity.
/// </summary>
public interface ITokenUsageStats
{
    /// <summary>
    /// The current token usage from completed requests within the time window.
    /// </summary>
    long CurrentUsage { get; }

    /// <summary>
    /// The total number of tokens currently reserved by active requests.
    /// </summary>
    long TotalReserved { get; }

    /// <summary>
    /// The number of tokens available for new reservations.
    /// </summary>
    long AvailableTokens { get; }

    /// <summary>
    /// The effective capacity after applying safety buffer (TokenLimit - SafetyBuffer).
    /// This is the maximum usable capacity for the system.
    /// </summary>
    long EffectiveCapacity { get; }

    /// <summary>
    /// The configured token limit per time window.
    /// </summary>
    long TokenLimit { get; }

    /// <summary>
    /// The current usage as a percentage of the token limit (0-100).
    /// </summary>
    double UsagePercentage { get; }

    /// <summary>
    /// The number of active reservations currently held.
    /// </summary>
    int ActiveReservationsCount { get; }

    /// <summary>
    /// The number of requests currently waiting for capacity.
    /// </summary>
    int WaitingRequestsCount { get; }

    /// <summary>
    /// The average efficiency of token estimation (actual tokens / reserved tokens).
    /// Values close to 1.0 indicate accurate estimation.
    /// Values significantly less than 1.0 indicate over-reservation.
    /// </summary>
    double AverageEstimationEfficiency { get; }
}
