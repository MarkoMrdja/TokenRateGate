using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;

namespace HighThroughputSample;

/// <summary>
/// Demonstrates "True Gate" behavior where MaxWaitTime = null.
/// In this mode, ALL requests eventually succeed - they just wait as long as needed for capacity.
/// This is ideal for batch processing, background jobs, or any scenario where guaranteed
/// processing is more important than fast response times.
/// </summary>
public static class TrueGateBehaviorExample
{
    /// <summary>
    /// Demonstrates that with MaxWaitTime = null, ALL requests succeed even under extreme load.
    /// This proves the "gate" metaphor - requests wait in line but all eventually get through.
    /// </summary>
    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("TRUE GATE BEHAVIOR - MaxWaitTime = null");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine();
        Console.WriteLine("Configuration:");
        Console.WriteLine("  - Token Limit: 50,000 tokens per 30 seconds");
        Console.WriteLine("  - Max Concurrent Requests: 3");
        Console.WriteLine("  - MaxWaitTime: null (INFINITE WAIT - true gate behavior)");
        Console.WriteLine("  - Total Requests: 30 (demanding ~112,500 tokens)");
        Console.WriteLine();
        Console.WriteLine("Expected Behavior:");
        Console.WriteLine("  ✓ ALL 30 requests should succeed (no timeouts)");
        Console.WriteLine("  ✓ Requests will queue and wait for capacity");
        Console.WriteLine("  ✓ Some requests may wait 30+ seconds (for window to roll)");
        Console.WriteLine("  ✓ Zero failures - guaranteed processing");
        Console.WriteLine();
        Console.WriteLine(new string('-', 80));
        Console.WriteLine();

        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,
            WindowSeconds = 30,
            MaxConcurrentRequests = 3,
            MaxWaitTime = null  // TRUE GATE: Infinite wait, all requests eventually succeed
        };

        using var rateGate = new TokenRateGate.Core.TokenRateGate(
            Options.Create(options),
            loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
        );

        var stopwatch = Stopwatch.StartNew();
        var results = new RequestResults();

        // Launch 30 requests that will demand more than capacity
        var tasks = Enumerable.Range(1, 30).Select(async requestId =>
        {
            var requestStartTime = stopwatch.Elapsed;

            try
            {
                var inputTokens = Random.Shared.Next(2000, 3000);
                var outputTokens = Random.Shared.Next(1000, 1500);

                Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds:F1}s] Request {requestId}: Requesting {inputTokens + outputTokens} tokens...");

                await using var reservation = await rateGate.ReserveTokensAsync(inputTokens, outputTokens);

                var waitTime = stopwatch.Elapsed - requestStartTime;
                results.RecordSuccess(requestId, waitTime);

                if (waitTime.TotalMilliseconds > 500)
                {
                    Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds:F1}s] Request {requestId}: ✓ Granted after {waitTime.TotalSeconds:F1}s wait");
                }

                // Simulate API processing
                await Task.Delay(Random.Shared.Next(500, 1000));

                var actualTotal = (int)(reservation.ReservedTokens * 0.95);
                reservation.RecordActualUsage(inputTokens, actualTotal - inputTokens);
            }
            catch (TimeoutException)
            {
                results.RecordTimeout(requestId);
                Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds:F1}s] Request {requestId}: ✗ TIMED OUT (This should NOT happen with MaxWaitTime = null!)");
            }
            catch (Exception ex)
            {
                results.RecordFailure(requestId, ex.Message);
                Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds:F1}s] Request {requestId}: ✗ FAILED: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Display results
        Console.WriteLine();
        Console.WriteLine(new string('=', 80));
        Console.WriteLine("RESULTS - True Gate Behavior");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine();

        Console.ForegroundColor = results.SuccessCount == 30 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"✓ Successful:  {results.SuccessCount}/30 requests");
        Console.ResetColor();

        if (results.TimeoutCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Timeouts:    {results.TimeoutCount}/30 requests (UNEXPECTED!)");
            Console.ResetColor();
        }

        if (results.FailureCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Failures:    {results.FailureCount}/30 requests");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("Wait Time Statistics:");
        Console.WriteLine($"  Average: {results.AverageWaitTime.TotalSeconds:F1}s");
        Console.WriteLine($"  Minimum: {results.MinWaitTime.TotalMilliseconds:F0}ms");
        Console.WriteLine($"  Maximum: {results.MaxWaitTime.TotalSeconds:F1}s");
        Console.WriteLine();
        Console.WriteLine($"Total Execution Time: {stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        // Verification
        if (results.SuccessCount == 30 && results.TimeoutCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓✓✓ VERIFICATION PASSED ✓✓✓");
            Console.WriteLine("All requests succeeded! True gate behavior confirmed.");
            Console.WriteLine("The library acts as a perfect gatekeeper - slowing down but never rejecting.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("✗✗✗ VERIFICATION FAILED ✗✗✗");
            Console.WriteLine($"Expected 30 successes, got {results.SuccessCount}.");
            Console.WriteLine("Some requests timed out or failed - this shouldn't happen with MaxWaitTime = null!");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 80));
        Console.WriteLine();
    }
}

/// <summary>
/// Thread-safe results collector
/// </summary>
class RequestResults
{
    private readonly object _lock = new();
    private readonly List<(int RequestId, TimeSpan WaitTime)> _successes = new();
    private readonly List<int> _timeouts = new();
    private readonly List<(int RequestId, string Error)> _failures = new();

    public int SuccessCount => _successes.Count;
    public int TimeoutCount => _timeouts.Count;
    public int FailureCount => _failures.Count;

    public TimeSpan AverageWaitTime
    {
        get
        {
            lock (_lock)
            {
                return _successes.Count > 0
                    ? TimeSpan.FromMilliseconds(_successes.Average(s => s.WaitTime.TotalMilliseconds))
                    : TimeSpan.Zero;
            }
        }
    }

    public TimeSpan MinWaitTime
    {
        get
        {
            lock (_lock)
            {
                return _successes.Count > 0 ? _successes.Min(s => s.WaitTime) : TimeSpan.Zero;
            }
        }
    }

    public TimeSpan MaxWaitTime
    {
        get
        {
            lock (_lock)
            {
                return _successes.Count > 0 ? _successes.Max(s => s.WaitTime) : TimeSpan.Zero;
            }
        }
    }

    public void RecordSuccess(int requestId, TimeSpan waitTime)
    {
        lock (_lock)
        {
            _successes.Add((requestId, waitTime));
        }
    }

    public void RecordTimeout(int requestId)
    {
        lock (_lock)
        {
            _timeouts.Add(requestId);
        }
    }

    public void RecordFailure(int requestId, string error)
    {
        lock (_lock)
        {
            _failures.Add((requestId, error));
        }
    }
}
