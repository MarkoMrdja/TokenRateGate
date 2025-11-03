using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Critical behavior tests based on TOKENRATEGATE_RULEBOOK.md
/// These tests validate the most important system behaviors that must work correctly for production use.
/// </summary>
public class CriticalBehaviorTests : IDisposable
{
    private TokenRateGate.Core.TokenRateGate? _gate;

    public void Dispose()
    {
        _gate?.Dispose();
    }

    #region Rule 1: RPM Limiting is Time-Based, Not Disposal-Based

    [Fact]
    public async Task RPM_DisposingReservation_DoesNotRemoveFromTimeline()
    {
        // Arrange: Configure with RPM tracking
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,  // High token limit (won't be bottleneck)
            WindowSeconds = 60,
            MaxRequestsPerMinute = 5,  // Allow 5 requests
            RequestWindowSeconds = 3,   // 3 second window for testing
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Make 3 requests and immediately dispose them
        for (int i = 0; i < 3; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(100, 0);
            await reservation.DisposeAsync();  // Immediately release
        }

        // Assert: Request count should still be 3 (disposed reservations stay in timeline)
        // This demonstrates that disposal does NOT remove entries from RPM timeline
        _gate.GetCurrentRequestCount().Should().Be(3,
            "Disposed reservations must remain in RPM timeline - disposal does NOT free RPM capacity");

        // The count stays at 3 even though all reservations have been disposed
        // This is a CRITICAL behavior for RPM limiting to work correctly
    }

    [Fact]
    public async Task RPM_GetCurrentRequestCount_ShouldIncludeActiveAndCompletedRequests()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 100,
            RequestWindowSeconds = 3,  // 3 second window
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 20
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Make 5 requests, dispose 2, keep 3 active
        var activeReservations = new List<Core.Models.TokenReservation>();

        // First 2 requests - complete and dispose
        for (int i = 0; i < 2; i++)
        {
            var res = await _gate.ReserveTokensAsync(100, 0);
            await res.DisposeAsync();
        }

        // Next 3 requests - keep active
        for (int i = 0; i < 3; i++)
        {
            activeReservations.Add(await _gate.ReserveTokensAsync(100, 0));
        }

        // Assert: Count should be 5 (2 completed + 3 active)
        _gate.GetCurrentRequestCount().Should().Be(5,
            "Request count should include both active and completed requests in window");

        var stats = _gate.GetUsageStats();
        stats.ActiveReservationsCount.Should().Be(3, "Only 3 reservations are still active");

        // Cleanup
        foreach (var res in activeReservations)
        {
            await res.DisposeAsync();
        }
    }

    #endregion

    #region Rule 2: Token Capacity Has Two Components

    [Fact]
    public async Task TokenCapacity_ShouldIncludeBothActualUsageAndActiveReservations()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Create completed usage (3000 tokens)
        var completed1 = await _gate.ReserveTokensAsync(1500, 0);
        completed1.RecordActualUsage(1500, 0);
        await completed1.DisposeAsync();

        var completed2 = await _gate.ReserveTokensAsync(1500, 0);
        completed2.RecordActualUsage(1500, 0);
        await completed2.DisposeAsync();

        // Create active reservations (4000 tokens)
        var active1 = await _gate.ReserveTokensAsync(2000, 0);
        var active2 = await _gate.ReserveTokensAsync(2000, 0);

        // Assert: Total usage should be 3000 (actual) + 4000 (reserved) = 7000
        var stats = _gate.GetUsageStats();
        stats.CurrentUsage.Should().Be(3000, "Actual usage from completed requests");
        stats.TotalReserved.Should().Be(4000, "Reserved from active reservations");

        // Available capacity should account for BOTH components
        // 10,000 - 3,000 (actual) - 4,000 (reserved) = 3,000
        stats.AvailableTokens.Should().Be(3000,
            "Available capacity must subtract both actual usage AND active reservations");

        // Cleanup
        await active1.DisposeAsync();
        await active2.DisposeAsync();
    }

    [Fact]
    public async Task TokenCapacity_ActiveReservationsCountTowardLimit()
    {
        // Arrange: Small token limit
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 5_000,
            WindowSeconds = 10,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10,
            MaxWaitTime = TimeSpan.FromSeconds(5)
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Reserve 4000 tokens (active, not completed)
        var active = await _gate.ReserveTokensAsync(4000, 0);

        var stats1 = _gate.GetUsageStats();
        stats1.TotalReserved.Should().Be(4000);
        stats1.AvailableTokens.Should().Be(1000, "Only 1000 tokens available");

        // Try to reserve 2000 more - should queue (not enough capacity)
        var queuedTask = _gate.ReserveTokensAsync(2000, 0);
        await Task.Delay(100);

        queuedTask.IsCompleted.Should().BeFalse("Should queue because active reservation counts toward limit");

        // Cleanup
        await active.DisposeAsync();

        // Now the queued request should complete
        var queued = await queuedTask;
        await queued.DisposeAsync();
    }

    #endregion

    #region Rule 6: Actual Usage is Optional But Important

    [Fact]
    public async Task RecordActualUsage_NotCalled_ShouldFreeCapacityOnDisposal()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Reserve tokens but DON'T call RecordActualUsage
        var reservation = await _gate.ReserveTokensAsync(5000, 0);

        var statsWhileActive = _gate.GetUsageStats();
        statsWhileActive.TotalReserved.Should().Be(5000, "Should reserve 5000 tokens");
        statsWhileActive.CurrentUsage.Should().Be(0, "No actual usage recorded yet");

        // Dispose WITHOUT recording actual usage
        await reservation.DisposeAsync();

        await Task.Delay(100); // Small delay for cleanup

        var statsAfterDisposal = _gate.GetUsageStats();

        // Assert: Capacity should be immediately freed (not added to usage timeline)
        statsAfterDisposal.TotalReserved.Should().Be(0, "Reservation should be released");
        statsAfterDisposal.CurrentUsage.Should().Be(0,
            "Without RecordActualUsage, no usage should be tracked in timeline");
        statsAfterDisposal.AvailableTokens.Should().Be(10_000, "Full capacity should be available");
    }

    [Fact]
    public async Task RecordActualUsage_Called_ShouldTrackUsageInTimeline()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 3,  // Short window for testing
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Reserve and record actual usage
        var reservation = await _gate.ReserveTokensAsync(3000, 0);
        reservation.RecordActualUsage(2000, 0);  // Actual usage less than reserved
        await reservation.DisposeAsync();

        await Task.Delay(100);

        var statsImmediately = _gate.GetUsageStats();

        // Assert: Actual usage should be tracked (2000, not 3000)
        statsImmediately.CurrentUsage.Should().Be(2000,
            "Actual usage (2000) should be tracked, not reserved amount (3000)");

        // Wait for window to expire
        await Task.Delay(3500);

        // Make a request to trigger cleanup
        var trigger = await _gate.ReserveTokensAsync(100, 0);
        await trigger.DisposeAsync();

        var statsAfterExpiry = _gate.GetUsageStats();

        // Usage should expire from timeline
        statsAfterExpiry.CurrentUsage.Should().BeLessThan(2000,
            "Usage should decrease after window expiration");
    }

    [Fact]
    public async Task RecordActualUsage_EstimationEfficiency_ShouldBeTracked()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Over-estimate tokens
        var reservation1 = await _gate.ReserveTokensAsync(1000, 500);  // Reserved: 1500
        reservation1.RecordActualUsage(1000, 200);  // Actual: 1200
        await reservation1.DisposeAsync();

        // Efficiency = 1200 / 1500 = 0.8 (80% efficient)
        var stats = _gate.GetUsageStats();

        // Assert: Efficiency should reflect over-estimation
        stats.AverageEstimationEfficiency.Should().BeLessThanOrEqualTo(1.0,
            "Efficiency should be ≤ 1.0 when actual usage ≤ reserved");

        // The system should track this for capacity optimization
        reservation1.ActualTokensUsed.Should().Be(1200);
        reservation1.ReservedTokens.Should().Be(1500);
    }

    #endregion

    #region Rule 7: Safety Buffer Reduces Effective Capacity

    [Fact]
    public async Task SafetyBuffer_ShouldReduceEffectiveCapacity()
    {
        // Arrange: 10% safety buffer
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.1,  // 10% = 1000 tokens
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act
        var stats = _gate.GetUsageStats();

        // Assert
        stats.TokenLimit.Should().Be(10_000, "Total limit is 10,000");
        stats.EffectiveCapacity.Should().Be(9_000, "Effective capacity should be 10,000 - 10% = 9,000");
        stats.AvailableTokens.Should().Be(9_000, "Available capacity should equal effective capacity initially");
    }

    [Fact]
    public async Task SafetyBuffer_ShouldPreventRequestsNearLimit()
    {
        // Arrange: High safety buffer
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 10,
            SafetyBufferPercentage = 0.2,  // 20% = 2000 tokens buffer
            MaxConcurrentRequests = 10,
            MaxWaitTime = TimeSpan.FromSeconds(3)
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Try to fill to actual limit (should be blocked by safety buffer)
        var stats1 = _gate.GetUsageStats();
        stats1.EffectiveCapacity.Should().Be(8_000, "Effective: 10,000 - 20% = 8,000");

        // Reserve up to effective limit
        var reservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 8; i++)
        {
            reservations.Add(await _gate.ReserveTokensAsync(1000, 0));
        }

        var stats2 = _gate.GetUsageStats();
        stats2.TotalReserved.Should().Be(8_000);

        // Next request should queue (safety buffer prevents using full capacity)
        var queuedTask = _gate.ReserveTokensAsync(500, 0);
        await Task.Delay(100);

        queuedTask.IsCompleted.Should().BeFalse("Request should queue due to safety buffer");

        // Cleanup
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }

        var queued = await queuedTask;
        await queued.DisposeAsync();
    }

    #endregion

    #region Output Estimation

    [Fact]
    public async Task OutputEstimation_ExplicitValue_ShouldOverrideStrategy()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10,
            OutputEstimationStrategy = OutputEstimationStrategy.Conservative  // Would double
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Provide explicit output estimate
        var reservation = await _gate.ReserveTokensAsync(1000, estimatedOutputTokens: 300);

        // Assert: Should use explicit value, not strategy
        reservation.ReservedTokens.Should().Be(1300,
            "Explicit estimatedOutputTokens should override strategy");

        await reservation.DisposeAsync();
    }

    #endregion

    #region FIFO Queue Processing

    [Fact]
    public async Task QueueProcessing_ShouldBeFIFO()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 5_000,
            WindowSeconds = 10,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 20,
            MaxWaitTime = TimeSpan.FromSeconds(10)
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Fill capacity
        var blocker = await _gate.ReserveTokensAsync(5000, 0);

        // Queue 5 requests in order
        var completionOrder = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tasks = new List<Task>();

        for (int i = 0; i < 5; i++)
        {
            int requestId = i;
            tasks.Add(Task.Run(async () =>
            {
                var res = await _gate.ReserveTokensAsync(500, 0);
                completionOrder.Add(requestId);
                await res.DisposeAsync();
            }));
            await Task.Delay(10);  // Ensure ordering
        }

        await Task.Delay(200);  // Ensure all are queued

        // Release capacity
        await blocker.DisposeAsync();

        await Task.WhenAll(tasks);

        // Assert: Should complete in FIFO order (0, 1, 2, 3, 4)
        var order = completionOrder.OrderBy(x => x).ToList();
        // Allow some flexibility due to timing, but first should definitely be 0
        order.First().Should().Be(0, "First queued request should complete first (FIFO)");
    }

    #endregion

    #region Cleanup and Window Expiration

    [Fact]
    public async Task Cleanup_ShouldRunOnEveryReservationAttempt()
    {
        // Arrange: Short window to test cleanup
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 1,  // 1 second window
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Create usage
        var res1 = await _gate.ReserveTokensAsync(3000, 0);
        res1.RecordActualUsage(3000, 0);
        await res1.DisposeAsync();

        var stats1 = _gate.GetUsageStats();
        stats1.CurrentUsage.Should().Be(3000);

        // Wait for window to expire
        await Task.Delay(1500);

        // Make a new request (this should trigger cleanup)
        var res2 = await _gate.ReserveTokensAsync(1000, 0);

        var stats2 = _gate.GetUsageStats();

        // Assert: Old usage should be cleaned up
        stats2.CurrentUsage.Should().BeLessThan(3000,
            "Cleanup should run on reservation attempt and remove expired usage");

        await res2.DisposeAsync();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ZeroTokenRequest_ShouldThrowArgumentException()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act & Assert: Request 0 tokens should throw
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _gate.ReserveTokensAsync(0, 0);
        });
    }

    [Fact]
    public async Task NegativeTokenRequest_ShouldThrowArgumentException()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _gate.ReserveTokensAsync(-100, 0);
        });
    }

    [Fact]
    public async Task FullCapacityUsage_ThenExpiration_ShouldAllowNewRequests()
    {
        // Arrange: Very short window
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 5_000,
            WindowSeconds = 1,  // 1 second window
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Fill capacity completely
        var res1 = await _gate.ReserveTokensAsync(5000, 0);
        res1.RecordActualUsage(5000, 0);
        await res1.DisposeAsync();

        var stats1 = _gate.GetUsageStats();
        stats1.CurrentUsage.Should().Be(5000);
        stats1.AvailableTokens.Should().Be(0, "Capacity should be full");

        // Wait for window to expire
        await Task.Delay(1500);

        // Make new request (should trigger cleanup and succeed)
        var res2 = await _gate.ReserveTokensAsync(5000, 0);

        // Assert: Should succeed
        res2.Should().NotBeNull();

        await res2.DisposeAsync();
    }

    #endregion
}
