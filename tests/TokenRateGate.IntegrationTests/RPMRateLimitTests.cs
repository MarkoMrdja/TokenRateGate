using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using TokenRateGate.Core;
using TokenRateGate.Core.Options;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests for Requests-Per-Minute (RPM) rate limiting functionality.
/// Validates that RPM limits work correctly alongside token-based limits.
/// </summary>
public class RPMRateLimitTests : IDisposable
{
    private TokenRateGate.Core.TokenRateGate? _gate;

    public void Dispose()
    {
        _gate?.Dispose();
    }

    [Fact]
    public async Task RPM_ShouldQueueWhenRequestLimitReached_EvenWithTokenCapacityAvailable()
    {
        // Arrange: Configure with low RPM limit but high token capacity, SHORT WINDOW for fast tests
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,  // High token limit
            WindowSeconds = 60,
            MaxRequestsPerMinute = 5,  // Low RPM limit - only 5 requests allowed
            RequestWindowSeconds = 3,  // Short window for fast testing
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Make 5 requests quickly (should succeed)
        var reservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 5; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(100, 0);
            reservations.Add(reservation);
        }

        // Verify we've hit the RPM limit
        var currentRequestCount = _gate.GetCurrentRequestCount();
        currentRequestCount.Should().Be(5, "Should have 5 requests in RPM window");

        // Act: 6th request should be queued due to RPM limit
        var queuedTask = _gate.ReserveTokensAsync(100, 0);

        // Wait a bit to ensure it's queued
        await Task.Delay(100);
        queuedTask.IsCompleted.Should().BeFalse("Request should be queued due to RPM limit");

        // RULEBOOK: Disposing does NOT free RPM capacity - must wait for window to slide
        await reservations[0].DisposeAsync();
        reservations.RemoveAt(0);

        // Request should STILL be queued (disposal doesn't free RPM)
        await Task.Delay(100);
        queuedTask.IsCompleted.Should().BeFalse("Disposal should not free RPM capacity");

        // Wait for the RPM window to slide (3s + margin)
        await Task.Delay(3500);

        // NOW the queued request should complete (window expired)
        var queuedReservation = await queuedTask.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        queuedReservation.Should().NotBeNull("Request should complete after window slides");

        // Cleanup
        await queuedReservation.DisposeAsync();
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task RPM_And_TPM_ShouldBothBeEnforced_WhicheverIsMoreRestrictive()
    {
        // Arrange: Configure with both limits active
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 5_000,  // Token limit
            WindowSeconds = 10,
            MaxRequestsPerMinute = 10,  // RPM limit
            RequestWindowSeconds = 10,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 20
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Scenario 1: Hit RPM limit first (many small requests)
        var smallReservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 10; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(100, 0);  // Small tokens
            smallReservations.Add(reservation);
        }

        // 11th request should be queued due to RPM
        using var cts1 = new CancellationTokenSource();
        var rpmBlockedTask = _gate.ReserveTokensAsync(100, 0, cts1.Token);
        await Task.Delay(100);
        rpmBlockedTask.IsCompleted.Should().BeFalse("Should be blocked by RPM limit");

        // Cancel the blocked request (must cancel BEFORE disposing)
        cts1.Cancel();

        try
        {
            await rpmBlockedTask;
            Assert.Fail("Expected cancellation exception");
        }
        catch (OperationCanceledException)
        {
            // Expected - request was cancelled
        }

        // Cleanup
        foreach (var res in smallReservations)
        {
            await res.DisposeAsync();
        }

        // Wait for gate to reset
        await Task.Delay(11_000);

        // Scenario 2: Hit token limit first (few large requests)
        var largeReservations = new List<Core.Models.TokenReservation>();

        // Fill token capacity with 5 large requests (1000 tokens each = 5000 total)
        for (int i = 0; i < 5; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(500, 500);  // 1000 tokens each
            largeReservations.Add(reservation);
        }

        var stats = _gate.GetUsageStats();
        stats.AvailableTokens.Should().BeLessThan(1000, "Should be near token capacity");

        // 6th request blocked by token limit (RPM still has room for 5 more)
        using var cts2 = new CancellationTokenSource();
        var tokenBlockedTask = _gate.ReserveTokensAsync(1000, 0, cts2.Token);
        await Task.Delay(100);
        tokenBlockedTask.IsCompleted.Should().BeFalse("Should be blocked by token limit");

        // Cancel the blocked request
        cts2.Cancel();

        try
        {
            await tokenBlockedTask;
            Assert.Fail("Expected cancellation exception");
        }
        catch (OperationCanceledException)
        {
            // Expected - request was cancelled
        }

        // Cleanup
        foreach (var res in largeReservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task RPM_Window_ShouldSlideCorrectly_RequestsExpireFromTimeline()
    {
        // Arrange: Short window for faster testing
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 3,  // Only 3 requests per minute
            RequestWindowSeconds = 3,  // 3 second window for testing
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Make 3 requests
        var reservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 3; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(100, 0);
            reservations.Add(reservation);
            await Task.Delay(100);  // Small delay between requests
        }

        // Verify we're at the limit
        _gate.GetCurrentRequestCount().Should().Be(3);

        // 4th request should be queued
        var sw = Stopwatch.StartNew();
        var queuedTask = _gate.ReserveTokensAsync(100, 0);

        await Task.Delay(200);
        queuedTask.IsCompleted.Should().BeFalse("Should be queued due to RPM");

        // Wait for window to slide (oldest request should expire)
        // We need to wait for the window (3s) plus some buffer for cleanup cycles
        await Task.Delay(3500);

        // Now the queued request should complete as oldest requests expired
        var queuedReservation = await queuedTask.WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        // Assert
        queuedReservation.Should().NotBeNull("Request should complete after window slides");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6),
            "Request should complete within reasonable time after window slide");

        // Cleanup
        await queuedReservation.DisposeAsync();
        foreach (var res in reservations)
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task RPM_ConcurrentRequests_ShouldBeHandledCorrectly()
    {
        // Arrange: SHORT WINDOW for fast testing
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 5,
            RequestWindowSeconds = 3,  // Short window for fast testing
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Act: Launch 10 concurrent requests (RPM limit is 5)
        var tasks = new List<Task<Core.Models.TokenReservation>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_gate.ReserveTokensAsync(100, 0));
        }

        // Wait a bit for processing
        await Task.Delay(500);

        // Assert: First 5 should complete, next 5 should be queued
        var completed = tasks.Where(t => t.IsCompleted).ToList();
        var pending = tasks.Where(t => !t.IsCompleted).ToList();

        completed.Count.Should().Be(5, "First 5 requests should complete immediately");
        pending.Count.Should().Be(5, "Next 5 requests should be queued");

        // RULEBOOK: Disposing does NOT free RPM capacity - must wait for window
        var completedReservations = await Task.WhenAll(completed);
        foreach (var res in completedReservations)
        {
            await res.DisposeAsync();
        }

        // Wait for RPM window to slide (3s + margin)
        await Task.Delay(3500);

        // Now queued requests should complete
        var allReservations = await Task.WhenAll(tasks);

        // All should eventually complete
        allReservations.Should().HaveCount(10);
        allReservations.Should().AllSatisfy(r => r.Should().NotBeNull());

        // Cleanup remaining
        foreach (var res in allReservations.Skip(5))
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public void RPM_Configuration_ShouldValidateCorrectly()
    {
        // Test 1: MaxRequestsPerMinute = 0 should throw
        var invalidOptions1 = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 0,  // Invalid
            SafetyBufferPercentage = 0.1,
            MaxConcurrentRequests = 5
        };

        Action act1 = () => new TokenRateGate.Core.TokenRateGate(
            Options.Create(invalidOptions1),
            NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        act1.Should().Throw<ArgumentException>()
            .WithMessage("*MaxRequestsPerMinute*");

        // Test 2: RequestWindowSeconds = 0 should throw
        var invalidOptions2 = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 100,
            RequestWindowSeconds = 0,  // Invalid
            SafetyBufferPercentage = 0.1,
            MaxConcurrentRequests = 5
        };

        Action act2 = () => new TokenRateGate.Core.TokenRateGate(
            Options.Create(invalidOptions2),
            NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        act2.Should().Throw<ArgumentException>()
            .WithMessage("*RequestWindowSeconds*");

        // Test 3: Valid configuration should not throw
        var validOptions = new TokenRateGateOptions
        {
            TokenLimit = 10_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 100,
            RequestWindowSeconds = 60,
            SafetyBufferPercentage = 0.1,
            MaxConcurrentRequests = 5
        };

        Action act3 = () =>
        {
            using var gate = new TokenRateGate.Core.TokenRateGate(
                Options.Create(validOptions),
                NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);
        };

        act3.Should().NotThrow();
    }

    [Fact]
    public async Task GetCurrentRequestCount_ShouldReturnAccurateCount()
    {
        // Arrange
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 100,
            RequestWindowSeconds = 60,
            SafetyBufferPercentage = 0.0,
            MaxConcurrentRequests = 10
        });

        _gate = new TokenRateGate.Core.TokenRateGate(options, NullLogger<TokenRateGate.Core.TokenRateGate>.Instance);

        // Test 1: Initial count should be 0
        _gate.GetCurrentRequestCount().Should().Be(0, "No requests made yet");

        // Test 2: Count should increase with active reservations
        var reservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 5; i++)
        {
            var reservation = await _gate.ReserveTokensAsync(100, 0);
            reservations.Add(reservation);
            _gate.GetCurrentRequestCount().Should().Be(i + 1, $"Should have {i + 1} active requests");
        }

        // Test 3: Count should remain after disposal (requests stay in timeline)
        await reservations[0].DisposeAsync();
        _gate.GetCurrentRequestCount().Should().Be(5, "Disposed reservations stay in request timeline");

        // Cleanup
        foreach (var res in reservations.Skip(1))
        {
            await res.DisposeAsync();
        }
    }

    [Fact]
    public async Task RPM_WithDisposedReservations_ShouldStillCountInWindow()
    {
        // Arrange: Short window for faster testing
        var options = Options.Create(new TokenRateGateOptions
        {
            TokenLimit = 100_000,
            WindowSeconds = 60,
            MaxRequestsPerMinute = 3,
            RequestWindowSeconds = 2,  // 2 second window
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

        // Assert: Even though disposed, they should count toward RPM
        _gate.GetCurrentRequestCount().Should().Be(3, "Disposed reservations still count in RPM window");

        // 4th request should still be queued
        var queuedTask = _gate.ReserveTokensAsync(100, 0);
        await Task.Delay(100);
        queuedTask.IsCompleted.Should().BeFalse("Should be queued despite token capacity being available");

        // Wait for window to expire (2s window + margin for cleanup)
        // Per RULEBOOK: requests expire after RequestWindowSeconds, need margin for cleanup
        await Task.Delay(2700);

        // Now it should complete (should be immediate after window expired)
        var queuedReservation = await queuedTask.WaitAsync(TimeSpan.FromSeconds(1));
        queuedReservation.Should().NotBeNull();

        await queuedReservation.DisposeAsync();
    }
}
