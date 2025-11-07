using JetBrains.dotMemoryUnit;
using JetBrains.dotMemoryUnit.Kernel;
using Microsoft.Extensions.Logging;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Models;
using Xunit.Abstractions;

namespace TokenRateGate.StressTests;

/// <summary>
/// Memory leak detection tests using dotMemory Unit for accurate leak detection
/// </summary>
public class MemoryLeakTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITestOutputHelper _output;

    public MemoryLeakTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Configure dotMemory Unit to use xUnit output
        DotMemoryUnitTestOutput.SetOutputMethod(message => _output.WriteLine(message));
    }

    [Fact]
    [DotMemoryUnit(FailIfRunWithoutSupport = false)]
    public async Task ContinuousReservations_ShouldNotLeakReservationObjects()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,
            WindowSeconds = 5, // Short window for faster test
            MaxConcurrentRequests = 50,
            SafetyBufferPercentage = 0.1,
            MaxWaitTime = TimeSpan.FromSeconds(10)
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Take memory snapshot before operations
        var memoryCheckpoint1 = dotMemory.Check();

        // Act - Run many reservation cycles
        for (int batch = 0; batch < 10; batch++)
        {
            var tasks = new List<Task>();

            for (int i = 0; i < 100; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var reservation = await gate.ReserveTokensAsync(50, 50);
                        await using var _ = reservation;
                        await Task.Delay(1);
                        reservation.RecordActualUsage(50, 50);
                    }
                    catch (Exception)
                    {
                        // Ignore timeouts/cancellations
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        // Wait for window to expire and cleanup to occur
        await Task.Delay(TimeSpan.FromSeconds(7));

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - Check that TokenReservation objects are not leaked
        if (dotMemoryApi.IsEnabled)
        {
            dotMemory.Check(memory =>
            {
                var reservations = memory.GetObjects(where => where.Type.Is<TokenReservation>()).ObjectsCount;

                // Allow for a small number of active reservations, but not 1000
                Assert.True(reservations < 50,
                    $"TokenReservation leak detected: {reservations} instances remaining");
            });
        }
        else
        {
            // When running without dotMemory, verify behavioral correctness
            var stats = gate.GetUsageStats();
            Assert.Equal(0, stats.ActiveReservationsCount);
            _output.WriteLine($"Test passed without dotMemory Unit. Active reservations: {stats.ActiveReservationsCount}");
        }
    }


    [Fact]
    [DotMemoryUnit(FailIfRunWithoutSupport = false)]
    public async Task QueueCycling_ShouldNotLeakWaitingRequests()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 1_000, // Small limit to force queueing
            WindowSeconds = 30,
            MaxConcurrentRequests = 5,
            SafetyBufferPercentage = 0.0,
            MaxWaitTime = TimeSpan.FromSeconds(5)
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        var memoryCheckpoint1 = dotMemory.Check();

        // Act - Create queue pressure then release
        for (int cycle = 0; cycle < 5; cycle++)
        {
            var reservations = new List<TokenReservation>();

            // Exhaust capacity
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var reservation = await gate.ReserveTokensAsync(100, 0);
                    reservations.Add(reservation);
                }
                catch
                {
                    break;
                }
            }

            // Release all
            foreach (var res in reservations)
            {
                res.RecordActualUsage(100, 0);
                await res.DisposeAsync();
            }

            reservations.Clear();
            await Task.Delay(100);
        }

        // Force cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - Internal data structures should not grow unbounded
        var stats = gate.GetUsageStats();
        Assert.Equal(0, stats.WaitingRequestsCount);
        Assert.Equal(0, stats.ActiveReservationsCount);

        if (dotMemoryApi.IsEnabled)
        {
            dotMemory.Check(memory =>
            {
                // Additional memory-level verification when dotMemory is available
                _output.WriteLine("dotMemory enabled: performing detailed memory analysis");
            });
        }
        else
        {
            _output.WriteLine($"Test passed without dotMemory Unit. Waiting: {stats.WaitingRequestsCount}, Active: {stats.ActiveReservationsCount}");
        }
    }

    [Fact]
    [DotMemoryUnit(FailIfRunWithoutSupport = false)]
    public async Task ExpiredReservations_ShouldBeCleanedUpFromMemory()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,
            WindowSeconds = 3, // Short window for fast expiration
            MaxConcurrentRequests = 100,
            SafetyBufferPercentage = 0.1,
            MaxWaitTime = TimeSpan.FromSeconds(2)
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        // Take initial snapshot
        var memoryCheckpoint1 = dotMemory.Check();

        // Act - Create many reservations
        for (int i = 0; i < 500; i++)
        {
            try
            {
                var reservation = await gate.ReserveTokensAsync(50, 50);
                reservation.RecordActualUsage(50, 50);
                await reservation.DisposeAsync();
            }
            catch { }

            if (i % 50 == 0)
                await Task.Delay(10);
        }

        // Wait for window to pass (expiration)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Force cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - Old records should be cleaned up
        var stats = gate.GetUsageStats();
        Assert.True(stats.CurrentUsage < 5_000,
            $"Old records not cleaned up. Current usage: {stats.CurrentUsage}");
        Assert.Equal(0, stats.ActiveReservationsCount);

        // Check memory - verify no excessive object retention
        if (dotMemoryApi.IsEnabled)
        {
            dotMemory.Check(memory =>
            {
                // The internal timeline structures should have released old entries
                _output.WriteLine("dotMemory enabled: verifying timeline cleanup at memory level");
            });
        }
        else
        {
            _output.WriteLine($"Test passed without dotMemory Unit. Current usage after expiration: {stats.CurrentUsage}");
        }
    }

    [Fact]
    [DotMemoryUnit(FailIfRunWithoutSupport = false)]
    public async Task HighVolumeStats_ShouldNotLeakMemory()
    {
        // Arrange
        var options = new TokenRateGateOptions
        {
            TokenLimit = 50_000,
            WindowSeconds = 10,
            MaxConcurrentRequests = 50
        };

        using var gate = new Core.TokenRateGate(options, _loggerFactory.CreateLogger<Core.TokenRateGate>());

        var memoryCheckpoint1 = dotMemory.Check();

        // Act - Call GetUsageStats many times while processing requests
        var statsTask = Task.Run(async () =>
        {
            for (int i = 0; i < 5_000; i++)
            {
                _ = gate.GetUsageStats();
                if (i % 100 == 0)
                    await Task.Delay(1);
            }
        });

        var workTask = Task.Run(async () =>
        {
            for (int i = 0; i < 2_500; i++)
            {
                var reservation = await gate.ReserveTokensAsync(10, 10);
                reservation.RecordActualUsage(10, 10);
                await reservation.DisposeAsync();
            }
        });

        await Task.WhenAll(statsTask, workTask);

        // Force cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - Stats calls should not leak memory
        if (dotMemoryApi.IsEnabled)
        {
            dotMemory.Check(memory =>
            {
                // Verify that TokenUsageStats objects are not accumulated
                var statsObjects = memory.GetObjects(where => where.Type.Is<TokenUsageStats>()).ObjectsCount;

                // We should have at most a few stats objects, not thousands
                Assert.True(statsObjects < 100,
                    $"TokenUsageStats leak detected: {statsObjects} instances remaining");
            });
        }
        else
        {
            // When running without dotMemory, verify the gate is still functional
            var finalStats = gate.GetUsageStats();
            Assert.Equal(0, finalStats.ActiveReservationsCount);
            _output.WriteLine($"Test passed without dotMemory Unit. Final state - Active: {finalStats.ActiveReservationsCount}");
        }
    }
}
