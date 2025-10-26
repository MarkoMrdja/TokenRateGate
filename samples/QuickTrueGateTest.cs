// Quick standalone test to verify true gate behavior
// Run with: dotnet script QuickTrueGateTest.cs

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;

Console.WriteLine("Testing True Gate Behavior (MaxWaitTime = null)");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Warning); // Only show warnings/errors
});

var options = new TokenRateGateOptions
{
    TokenLimit = 10_000,      // Small limit to force queuing
    WindowSeconds = 10,        // Short window for faster test
    MaxConcurrentRequests = 2, // Only 2 at a time
    MaxWaitTime = null         // TRUE GATE: Infinite wait
};

Console.WriteLine($"Config: {options.TokenLimit} tokens per {options.WindowSeconds}s, MaxWait = null");
Console.WriteLine($"Launching 10 requests demanding ~35,000 tokens (3.5x capacity)");
Console.WriteLine();

using var gate = new TokenRateGate.Core.TokenRateGate(
    Options.Create(options),
    loggerFactory.CreateLogger<TokenRateGate.Core.TokenRateGate>()
);

var sw = Stopwatch.StartNew();
int successCount = 0;
int failureCount = 0;

var tasks = Enumerable.Range(1, 10).Select(async i =>
{
    try
    {
        var start = sw.Elapsed;
        await using var reservation = await gate.ReserveTokensAsync(2500, 1000);
        var wait = sw.Elapsed - start;

        Interlocked.Increment(ref successCount);
        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Request {i}: SUCCESS (waited {wait.TotalSeconds:F1}s)");

        await Task.Delay(100); // Simulate work
        reservation.RecordActualUsage(2500, 1000);
    }
    catch (Exception ex)
    {
        Interlocked.Increment(ref failureCount);
        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] Request {i}: FAILED - {ex.Message}");
    }
});

await Task.WhenAll(tasks);
sw.Stop();

Console.WriteLine();
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine($"RESULTS: {successCount} successes, {failureCount} failures in {sw.Elapsed.TotalSeconds:F1}s");

if (successCount == 10 && failureCount == 0)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("✓✓✓ TEST PASSED ✓✓✓ All requests succeeded!");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"✗✗✗ TEST FAILED ✗✗✗ Expected 10 successes, got {successCount}");
    Console.ResetColor();
}
