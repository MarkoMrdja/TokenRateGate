using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TokenRateGate.Core.Options;
using TokenRateGate.Core.Utils;
using TokenRateGate.Extensions.DependencyInjection;

namespace TokenRateGate.IntegrationTests;

/// <summary>
/// Integration tests for multi-tenant scenarios using TokenRateGateFactory.
/// Tests real-world patterns of multiple tenants/API keys with different rate limits
/// operating concurrently with full isolation.
/// </summary>
public class MultiTenantIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public MultiTenantIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        // Configure factory with multiple tenant configurations
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("tenant-small", options =>
            {
                options.TokenLimit = 10_000;
                options.WindowSeconds = 60;
                options.SafetyBufferPercentage = 0.05;
                options.MaxConcurrentRequests = 20; // Increased to handle parallel tests
            });

            builder.AddGate("tenant-medium", options =>
            {
                options.TokenLimit = 50_000;
                options.WindowSeconds = 60;
                options.SafetyBufferPercentage = 0.05;
                options.MaxConcurrentRequests = 20; // Increased to handle parallel tests
            });

            builder.AddGate("tenant-large", options =>
            {
                options.TokenLimit = 100_000;
                options.WindowSeconds = 60;
                options.SafetyBufferPercentage = 0.05;
                options.MaxConcurrentRequests = 20;
            });

            builder.AddGate("tenant-enterprise", options =>
            {
                options.TokenLimit = 500_000;
                options.WindowSeconds = 60;
                options.SafetyBufferPercentage = 0.03;
                options.MaxConcurrentRequests = 50;
            });
        });

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task MultipleNamedGates_WithDifferentConfigurations_ShouldOperateIndependently()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        var smallGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-small");
        var mediumGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-medium");
        var largeGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-large");

        // Act - Make requests to each tenant
        await using var reservation1 = await smallGate.ReserveTokensAsync(1000, 500);
        await using var reservation2 = await mediumGate.ReserveTokensAsync(5000, 2500);
        await using var reservation3 = await largeGate.ReserveTokensAsync(10000, 5000);

        var smallStats = smallGate.GetUsageStats();
        var mediumStats = mediumGate.GetUsageStats();
        var largeStats = largeGate.GetUsageStats();

        // Assert - Each gate tracks its own usage independently
        smallStats.TotalReserved.Should().Be(1500);
        mediumStats.TotalReserved.Should().Be(7500);
        largeStats.TotalReserved.Should().Be(15000);

        // Verify percentages are relative to each gate's limit (percentage is 0-100, not 0-1)
        smallStats.UsagePercentage.Should().BeApproximately(15.0, 0.5); // 1500/10000 = 15%
        mediumStats.UsagePercentage.Should().BeApproximately(15.0, 0.5); // 7500/50000 = 15%
        largeStats.UsagePercentage.Should().BeApproximately(15.0, 0.5); // 15000/100000 = 15%
    }

    [Fact]
    public async Task ConcurrentAccessFromMultipleTenants_ShouldNotInterfere()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        var tenant1Gate = (Core.TokenRateGate)factory.GetOrCreate("tenant-small");
        var tenant2Gate = (Core.TokenRateGate)factory.GetOrCreate("tenant-medium");

        // Act - Simulate concurrent requests from both tenants
        var tasks = new List<Task<Core.Models.TokenReservation>>();

        // Tenant 1: 10 requests
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(tenant1Gate.ReserveTokensAsync(500, 250));
        }

        // Tenant 2: 10 requests
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(tenant2Gate.ReserveTokensAsync(2000, 1000));
        }

        var reservations = await Task.WhenAll(tasks);

        var tenant1Stats = tenant1Gate.GetUsageStats();
        var tenant2Stats = tenant2Gate.GetUsageStats();

        // Assert
        tenant1Stats.TotalReserved.Should().Be(7500); // 10 * 750 tokens
        tenant2Stats.TotalReserved.Should().Be(30000); // 10 * 3000 tokens

        // Cleanup
        foreach (var reservation in reservations)
        {
            await reservation.DisposeAsync();
        }
    }

    [Fact]
    public async Task CrossTenantIsolation_OneTenantExhaustingCapacity_ShouldNotAffectOthers()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        var limitedGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-small");
        var unaffectedGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-large");

        // Act - Fill up the small tenant's capacity (but not exceed it)
        // With 10K limit and 5% safety buffer = 9,500 effective capacity
        // Reserve 10 * 900 = 9,000 tokens (safely below limit)
        var limitedReservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 10; i++)
        {
            limitedReservations.Add(await limitedGate.ReserveTokensAsync(900, 0));
        }

        var limitedStats = limitedGate.GetUsageStats();

        // The large tenant should still have full capacity
        await using var unaffectedReservation = await unaffectedGate.ReserveTokensAsync(10000, 5000);
        var unaffectedStats = unaffectedGate.GetUsageStats();

        // Assert
        limitedStats.UsagePercentage.Should().BeGreaterThanOrEqualTo(90.0); // At or over 90% utilized
        unaffectedStats.TotalReserved.Should().Be(15000);
        unaffectedStats.UsagePercentage.Should().BeLessThan(20.0); // Under 20% utilized

        // Cleanup
        foreach (var reservation in limitedReservations)
        {
            await reservation.DisposeAsync();
        }
    }

    [Fact]
    public void FactoryGetOrCreate_ShouldCacheInstances()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();

        // Act
        var gate1 = factory.GetOrCreate("tenant-small");
        var gate2 = factory.GetOrCreate("tenant-small");
        var gate3 = factory.GetOrCreate("TENANT-SMALL"); // Case insensitive

        // Assert
        gate1.Should().BeSameAs(gate2);
        gate2.Should().BeSameAs(gate3);
    }

    [Fact]
    public async Task FactoryGetOrCreate_ThreadSafety_ConcurrentCreation()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        const int threadCount = 50;
        const string tenantName = "concurrent-tenant";

        // Act - Try to create the same gate from many threads simultaneously
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() => factory.GetOrCreate(tenantName)))
            .ToArray();

        var gates = await Task.WhenAll(tasks);

        // Assert - All threads should get the same instance
        gates.Should().AllBeEquivalentTo(gates[0]);
        gates.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task RealWorldMultiTenantPattern_DifferentUsageProfiles()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        var smallGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-small");
        var mediumGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-medium");
        var largeGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-large");

        // Act - Simulate real-world usage patterns
        var tasks = new List<Task>();

        // Small tenant: Few large requests
        tasks.Add(Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await using var reservation = await smallGate.ReserveTokensAsync(2000, 1000);
                await Task.Delay(50);
                reservation.RecordActualUsage(2000, 900);
            }
        }));

        // Medium tenant: Moderate number of medium requests
        tasks.Add(Task.Run(async () =>
        {
            for (int i = 0; i < 8; i++)
            {
                await using var reservation = await mediumGate.ReserveTokensAsync(3000, 2000);
                await Task.Delay(30);
                reservation.RecordActualUsage(3000, 1800);
            }
        }));

        // Large tenant: Many small requests
        tasks.Add(Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await using var reservation = await largeGate.ReserveTokensAsync(1500, 1000);
                await Task.Delay(20);
                reservation.RecordActualUsage(1500, 950);
            }
        }));

        await Task.WhenAll(tasks);

        // Allow cleanup to process
        await Task.Delay(200);

        // Assert - Each tenant's actual usage should be tracked
        var smallStats = smallGate.GetUsageStats();
        var mediumStats = mediumGate.GetUsageStats();
        var largeStats = largeGate.GetUsageStats();

        smallStats.CurrentUsage.Should().BeGreaterThan(0);
        mediumStats.CurrentUsage.Should().BeGreaterThan(0);
        largeStats.CurrentUsage.Should().BeGreaterThan(0);

        // All reservations should be released
        smallStats.TotalReserved.Should().Be(0);
        mediumStats.TotalReserved.Should().Be(0);
        largeStats.TotalReserved.Should().Be(0);
    }

    [Fact]
    public async Task MultiTenantQueuing_OneTenantsQueueDoesNotBlockAnother()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        var constrainedGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-small");
        var freeGate = (Core.TokenRateGate)factory.GetOrCreate("tenant-enterprise");

        // Act - Fill up constrained gate capacity completely
        // With 10K limit and 5% safety buffer = 9,500 effective capacity
        // Reserve 9,500 tokens to fill completely
        var activeReservations = new List<Core.Models.TokenReservation>();
        for (int i = 0; i < 10; i++)
        {
            activeReservations.Add(await constrainedGate.ReserveTokensAsync(950, 0));
        }

        // Start a request that will have to wait (capacity is exhausted)
        var waitingTask = Task.Run(async () => await constrainedGate.ReserveTokensAsync(500, 0));
        await Task.Delay(200); // Give it time to queue

        var constrainedStats = constrainedGate.GetUsageStats();

        // Meanwhile, the enterprise tenant should have no delays
        var startTime = DateTime.UtcNow;
        await using var enterpriseReservation = await freeGate.ReserveTokensAsync(50000, 25000);
        var duration = DateTime.UtcNow - startTime;

        // Assert
        constrainedStats.WaitingRequestsCount.Should().BeGreaterThan(0);
        duration.Should().BeLessThan(TimeSpan.FromMilliseconds(100)); // Should be instant

        // Cleanup - release one reservation to unblock the waiting task
        await activeReservations[0].DisposeAsync();
        var waitingReservation = await waitingTask;
        await waitingReservation.DisposeAsync();

        foreach (var reservation in activeReservations.Skip(1))
        {
            await reservation.DisposeAsync();
        }
    }

    [Fact]
    public async Task TenantWithDifferentConfigurations_ShouldWorkIndependently()
    {
        // Arrange - Create factory with tenants having different token limits
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("tenant-config-a", options =>
            {
                options.TokenLimit = 20_000;
                options.WindowSeconds = 60;
                options.SafetyBufferPercentage = 0.05;
            });

            builder.AddGate("tenant-config-b", options =>
            {
                options.TokenLimit = 30_000;
                options.WindowSeconds = 90;
                options.SafetyBufferPercentage = 0.10;
            });

            builder.AddGate("tenant-config-c", options =>
            {
                options.TokenLimit = 40_000;
                options.WindowSeconds = 120;
                options.SafetyBufferPercentage = 0.03;
            });
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        var gateA = (Core.TokenRateGate)factory.GetOrCreate("tenant-config-a");
        var gateB = (Core.TokenRateGate)factory.GetOrCreate("tenant-config-b");
        var gateC = (Core.TokenRateGate)factory.GetOrCreate("tenant-config-c");

        // Act - Make reservations with explicit output tokens
        await using var reservationA = await gateA.ReserveTokensAsync(1000, 500);
        await using var reservationB = await gateB.ReserveTokensAsync(2000, 1000);
        await using var reservationC = await gateC.ReserveTokensAsync(3000, 1500);

        var statsA = gateA.GetUsageStats();
        var statsB = gateB.GetUsageStats();
        var statsC = gateC.GetUsageStats();

        // Assert - Each tenant tracks its usage independently with its own limits
        reservationA.ReservedTokens.Should().Be(1500);
        reservationB.ReservedTokens.Should().Be(3000);
        reservationC.ReservedTokens.Should().Be(4500);

        statsA.TokenLimit.Should().Be(20_000);
        statsB.TokenLimit.Should().Be(30_000);
        statsC.TokenLimit.Should().Be(40_000);
    }

    [Fact]
    public async Task FactoryDisposal_ShouldDisposeAllManagedGates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddTokenRateGateFactory(builder =>
        {
            builder.AddGate("disposable-1", options => options.TokenLimit = 10000);
            builder.AddGate("disposable-2", options => options.TokenLimit = 10000);
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<TokenRateGateFactory>();

        // Create some gates
        factory.GetOrCreate("disposable-1");
        factory.GetOrCreate("disposable-2");

        // Act
        factory.Dispose();

        // Assert - Should not be able to use factory after disposal
        var act = () => factory.GetOrCreate("disposable-1");
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void FactoryMethods_ShouldProvideComprehensiveGateManagement()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();

        // Act & Assert - Get method
        factory.Get("nonexistent").Should().BeNull();

        var created = factory.GetOrCreate("management-test");
        var retrieved = factory.Get("management-test");
        retrieved.Should().BeSameAs(created);

        // Exists method
        factory.Exists("management-test").Should().BeTrue();
        factory.Exists("nonexistent").Should().BeFalse();

        // GetAllNames method
        var allNames = factory.GetAllNames().ToList();
        allNames.Should().Contain("management-test");
    }

    [Fact]
    public async Task HighConcurrencyAcrossMultipleTenants_ShouldMaintainIsolation()
    {
        // Arrange
        var factory = _serviceProvider.GetRequiredService<TokenRateGateFactory>();
        const int requestsPerTenant = 50;
        var tenants = new[] { "tenant-small", "tenant-medium", "tenant-large" };

        // Act - Simulate high concurrent load across all tenants
        var allTasks = tenants.SelectMany(tenantName =>
        {
            var gate = (Core.TokenRateGate)factory.GetOrCreate(tenantName);
            return Enumerable.Range(0, requestsPerTenant).Select(_ =>
                Task.Run(async () =>
                {
                    await using var reservation = await gate.ReserveTokensAsync(100, 50);
                    await Task.Delay(Random.Shared.Next(1, 10));
                    reservation.RecordActualUsage(100, 45);
                })
            );
        }).ToList();

        await Task.WhenAll(allTasks);

        // Assert - All tenants should have processed their requests
        foreach (var tenantName in tenants)
        {
            var gate = (Core.TokenRateGate)factory.GetOrCreate(tenantName);
            var stats = gate.GetUsageStats();

            // All reservations should be released
            stats.TotalReserved.Should().Be(0);
            stats.WaitingRequestsCount.Should().Be(0);
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
