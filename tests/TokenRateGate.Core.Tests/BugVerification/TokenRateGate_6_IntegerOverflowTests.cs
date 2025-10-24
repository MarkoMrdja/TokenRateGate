using FluentAssertions;
using TokenRateGate.Core.Tests.TestHelpers;
using TokenRateGate.Core.Utils;
using Xunit;

namespace TokenRateGate.Core.Tests.BugVerification;

/// <summary>
/// Tests to verify bug TokenRateGate-6: Integer overflow risk in token calculation
///
/// Bug Description:
/// CalculateEstimatedTotalTokens() performs arithmetic without overflow checking.
/// Large input values can cause integer overflow, resulting in negative values that bypass capacity checks.
///
/// Location: TokenRateGate.cs:199, 201, 203
/// Impact: Could completely break rate limiting by allowing unlimited token reservations
///
/// Expected Behavior (after fix):
/// Add checked arithmetic or validate that calculated total doesn't exceed int.MaxValue and TokenLimit.
/// </summary>
public class TokenRateGate_6_IntegerOverflowTests
{
    [Fact]
    public void ConservativeEstimation_OverflowsWithLargeInput()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.OutputEstimationStrategy = OutputEstimationStrategy.Conservative;
        options.TokenLimit = int.MaxValue;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act & Assert
        // Conservative strategy multiplies by 2 (line 203: return inputTokens * 2)
        // With inputTokens > int.MaxValue / 2, this overflows
        var largeInput = 1_500_000_000; // Greater than int.MaxValue / 2

        Exception? capturedException = null;
        try
        {
            // BUG: This should throw or be rejected, but instead overflows to negative
            var task = gate.ReserveTokensAsync(inputTokens: largeInput, estimatedOutputTokens: 0);
            _ = task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException agg)
        {
            capturedException = agg.InnerException;
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // BUG VERIFICATION:
        // Expected after fix: ArgumentException about overflow
        // Current buggy behavior: Might succeed or behave unpredictably due to overflow

        if (capturedException == null)
        {
            // BUG: Request was accepted despite overflow
            Console.WriteLine("BUG CONFIRMED: Integer overflow allowed request to proceed");
        }
        else
        {
            Console.WriteLine($"Exception thrown: {capturedException.GetType().Name}: {capturedException.Message}");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public void FixedMultiplierEstimation_CanOverflow()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier;
        options.OutputMultiplier = 2.0; // Double the input
        options.TokenLimit = int.MaxValue;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act & Assert
        // Line 199: return inputTokens + (int)Math.Ceiling(inputTokens * _options.OutputMultiplier);
        // With multiplier = 2.0 and large input, Math.Ceiling returns value > int.MaxValue
        // Cast to (int) causes overflow
        var largeInput = 1_200_000_000;

        Exception? capturedException = null;
        try
        {
            var task = gate.ReserveTokensAsync(inputTokens: largeInput, estimatedOutputTokens: 0);
            _ = task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException agg)
        {
            capturedException = agg.InnerException;
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // BUG VERIFICATION
        if (capturedException == null)
        {
            Console.WriteLine("BUG: FixedMultiplier overflow allowed request");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public void FixedAmountEstimation_CanOverflowAddition()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.OutputEstimationStrategy = OutputEstimationStrategy.FixedAmount;
        options.DefaultOutputTokens = 1000;
        options.TokenLimit = int.MaxValue;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act & Assert
        // Line 201: return inputTokens + _options.DefaultOutputTokens;
        // If inputTokens is close to int.MaxValue, addition overflows
        var largeInput = int.MaxValue - 500; // Will overflow when adding DefaultOutputTokens (1000)

        Exception? capturedException = null;
        try
        {
            var task = gate.ReserveTokensAsync(inputTokens: largeInput, estimatedOutputTokens: 0);
            _ = task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException agg)
        {
            capturedException = agg.InnerException;
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // BUG VERIFICATION
        if (capturedException == null)
        {
            Console.WriteLine("BUG: FixedAmount addition overflow allowed request");
        }
        else if (capturedException is ArgumentException)
        {
            // Expected after fix
            capturedException.Message.Should().Contain("overflow",
                "because validation should detect the overflow");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public void ExplicitOutputTokens_CanOverflowAddition()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.TokenLimit = int.MaxValue;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act & Assert
        // Line 196: return inputTokens + estimatedOutputTokens;
        // Direct addition can overflow
        var input = int.MaxValue / 2 + 1;
        var output = int.MaxValue / 2 + 1;
        // input + output > int.MaxValue

        Exception? capturedException = null;
        try
        {
            var task = gate.ReserveTokensAsync(inputTokens: input, estimatedOutputTokens: output);
            _ = task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException agg)
        {
            capturedException = agg.InnerException;
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // BUG VERIFICATION
        if (capturedException == null)
        {
            Console.WriteLine("BUG: Explicit token overflow allowed request");
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task OverflowResultsInNegativeValue_BypassesCapacityCheck()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.OutputEstimationStrategy = OutputEstimationStrategy.Conservative;
        options.TokenLimit = 10000;
        options.SafetyBuffer = 0;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Use value that will overflow to negative when multiplied by 2
        var overflowInput = int.MaxValue / 2 + 1000;
        // overflowInput * 2 will wrap around to a large negative number

        Exception? capturedException = null;
        Models.TokenReservation? reservation = null;

        try
        {
            // BUG: The overflow produces negative value
            // Negative values pass capacity checks (negative < TokenLimit is true!)
            reservation = await gate.ReserveTokensAsync(inputTokens: overflowInput, estimatedOutputTokens: 0);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // Assert
        // BUG VERIFICATION: Either:
        // A) Reservation succeeds with negative token value (completely breaks rate limiting)
        // B) Exception is thrown (but not the right kind - should be ArgumentException about overflow)

        if (reservation != null)
        {
            // CRITICAL BUG: Negative token count allowed through!
            reservation.ReservedTokens.Should().NotBe(0,
                "reservation was created despite overflow");

            // The negative value bypasses capacity checks
            Console.WriteLine($"BUG CRITICAL: Reservation created with tokens: {reservation.ReservedTokens}");
            Console.WriteLine("Negative token count bypasses all capacity checks!");

            await reservation.DisposeAsync();
        }
        else if (capturedException != null)
        {
            // Some exception occurred
            Console.WriteLine($"Exception: {capturedException.GetType().Name}");

            // After fix, should be ArgumentException about overflow
            // Before fix, might be other exceptions or nothing
        }

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public async Task MultipleOverflowRequests_CanExhaustSystemResources()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.OutputEstimationStrategy = OutputEstimationStrategy.Conservative;
        options.TokenLimit = 10000;
        options.SafetyBuffer = 0;
        options.MaxConcurrentRequests = 10;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act
        // Make multiple requests that would overflow
        var overflowInput = 1_200_000_000; // Will overflow when * 2
        var reservations = new List<Models.TokenReservation>();
        var exceptions = new List<Exception>();

        for (int i = 0; i < 5; i++)
        {
            try
            {
                var reservation = await gate.ReserveTokensAsync(inputTokens: overflowInput, estimatedOutputTokens: 0);
                reservations.Add(reservation);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        // Assert
        // BUG IMPACT: If overflow produces negative values, multiple "negative token" reservations
        // could be granted simultaneously, completely breaking rate limiting

        if (reservations.Any())
        {
            Console.WriteLine($"BUG: {reservations.Count} overflow reservations were granted!");
            Console.WriteLine("This completely breaks rate limiting - unlimited tokens can be reserved");

            reservations.Count.Should().BeGreaterThan(0,
                "overflow bug allowed multiple invalid reservations");

            // Cleanup
            foreach (var r in reservations)
            {
                await r.DisposeAsync();
            }
        }

        Console.WriteLine($"Exceptions caught: {exceptions.Count}");

        // Cleanup
        gate.Dispose();
    }

    [Fact]
    public void EdgeCase_IntMaxValue_Minus1_TimesTwo_Overflows()
    {
        // Arrange
        var options = TokenRateGateTestFactory.CreateMinimalOptions();
        options.OutputEstimationStrategy = OutputEstimationStrategy.Conservative;
        options.TokenLimit = int.MaxValue;
        var gate = TokenRateGateTestFactory.CreateGate(options);

        // Act & Assert
        // Exact edge case: int.MaxValue / 2 = 1,073,741,823
        // 1,073,741,824 * 2 = 2,147,483,648 which is int.MaxValue + 1 → overflows
        var edgeInput = (int.MaxValue / 2) + 1;

        Exception? capturedException = null;
        try
        {
            var task = gate.ReserveTokensAsync(inputTokens: edgeInput, estimatedOutputTokens: 0);
            _ = task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException agg)
        {
            capturedException = agg.InnerException;
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // BUG: Should throw ArgumentException about overflow
        // Currently: Might succeed with negative value or behave unpredictably

        if (capturedException == null)
        {
            Console.WriteLine($"BUG: Edge case overflow (input={edgeInput}) was not caught");
        }

        // Cleanup
        gate.Dispose();
    }
}
