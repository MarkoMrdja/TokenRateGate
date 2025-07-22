using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core.Options;

namespace TokenRateGate.Core;

public class TokenRateGate
{
    // ============================================================================
    // CONFIGURATION AND DEPENDENCIES
    // ============================================================================

    private readonly TokenRateGateOptions _options;
    private readonly ILogger<TokenRateGate> _logger;

    private readonly object _lock = new();

    // ============================================================================
    // TOKEN TRACKING
    // ============================================================================

    // Completed requests timeline
    private readonly Queue<TokenUsageEntry> _usageTimeline = new();
    private int _currentActualUsage = 0;

    // Executing requests
    private readonly Dictionary<Guid, PendingReservations> _activeReservations = new();

    // Waiting requests
    private readonly LinkedList<WaitingRequests> _waitingRequests = new();

    // Request timeline for rate limiting
    private readonly Queue<DateTime> _requestTimeline = new();

    private readonly SemaphoreSlim _concurrencyLimiter;

    private readonly Timer _safetyTimer;
    private bool _disposed = false;

    private DateTime _lastCleanup = DateTime.MinValue;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(3); // TODO: Consider making this configurable

    // ============================================================================
    // CONSTRUCTORS
    // ============================================================================

    public TokenRateGate(IOptions<TokenRateGateOptions> options, ILogger<TokenRateGate> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
    }

    public TokenRateGate(TokenRateGateOptions options, ILogger<TokenRateGate> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
    }

    // ============================================================================
    // MAIN INTERFACE IMPLEMENTATION
    // ============================================================================



    // ============================================================================
    // SUPPORTING TYPES
    // ============================================================================

    private record TokenUsageEntry(DateTime Timestamp, int Tokens);
    private record PendingReservations(Guid Id, int EstimatedTokens, DateTime Timestamp);
    private class WaitingRequests
    {
        public int RequiredTokens { get; }
        public CancellationToken CancellationToken { get; set; }
        public TaskCompletionSource<Guid> TaskCompletionSource { get; }
        public LinkedListNode<WaitingRequests>? Node { get; set; }
        public Guid ReservationId { get; set; }

        public WaitingRequests(int requiredTokens, CancellationToken cancellationToken)
        {
            RequiredTokens = requiredTokens;
            CancellationToken = cancellationToken;
            TaskCompletionSource = new TaskCompletionSource<Guid>();
        }
    }
}
