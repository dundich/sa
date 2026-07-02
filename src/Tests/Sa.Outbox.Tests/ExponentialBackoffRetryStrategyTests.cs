using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

public class ExponentialBackoffRetryStrategyTests
{
    [Fact]
    public void Shared_Instance_Is_Not_Null()
    {
        Assert.NotNull(ExponentialBackoffRetryStrategy.Shared);
    }

    [Fact]
    public void GetBackoff_FirstAttempt_Returns_Base_Delay_Range()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(5),
            maxDelay: TimeSpan.FromMinutes(30));

        var backoff = strategy.GetBackoff(1);

        // With jitter 0.5..1.0, first attempt (2^0 = 1) should be between 2.5s and 5s
        Assert.True(backoff >= TimeSpan.FromSeconds(2.4));
        Assert.True(backoff <= TimeSpan.FromSeconds(5.1));
    }

    [Fact]
    public void GetBackoff_SecondAttempt_Returns_Double_Base_Delay_Range()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(5),
            maxDelay: TimeSpan.FromMinutes(30));

        var backoff = strategy.GetBackoff(2);

        // Second attempt: 5 * 2^1 = 10s, with jitter 0.5..1.0 → 5s..10s
        Assert.True(backoff >= TimeSpan.FromSeconds(4.9));
        Assert.True(backoff <= TimeSpan.FromSeconds(10.1));
    }

    [Fact]
    public void GetBackoff_HighAttempt_Capped_At_MaxDelay()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(10));

        var backoff = strategy.GetBackoff(10);

        // 2^9 = 512, but capped at 10s, with jitter 0.5..1.0 → 5s..10s
        Assert.True(backoff >= TimeSpan.FromSeconds(4.9));
        Assert.True(backoff <= TimeSpan.FromSeconds(10.1));
    }

    [Fact]
    public void GetBackoff_IncreasingAttempts_Yield_IncreasingDelays_UntilCap()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromMinutes(5));

        var b1 = strategy.GetBackoff(1);
        var b2 = strategy.GetBackoff(2);
        var b3 = strategy.GetBackoff(3);

        // Generally increasing (jitter may cause occasional inversion, but trend should hold)
        // We test the deterministic part: 1, 2, 4 seconds
        Assert.True(b1 < b3);
        Assert.True(b2 < b3);
    }

    [Fact]
    public void GetBackoff_AttemptZero_Treated_As_One()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(5),
            maxDelay: TimeSpan.FromMinutes(30));

        var backoff0 = strategy.GetBackoff(0);
        var backoff1 = strategy.GetBackoff(1);

        // Both should fall in the same range (2.5s..5s)
        Assert.True(backoff0 >= TimeSpan.FromSeconds(2.4));
        Assert.True(backoff0 <= TimeSpan.FromSeconds(5.1));
    }

    [Fact]
    public void Constructor_Clamps_Jitter_Factors()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            jitterFactorMin: -0.5,  // clamped to 0
            jitterFactorMax: 1.5);  // clamped to 1.0

        var backoff = strategy.GetBackoff(1);
        // Should still produce a valid result
        Assert.True(backoff > TimeSpan.Zero);
    }

    [Fact]
    public void GetBackoff_Custom_Jitter_Produces_Narrower_Range()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(10),
            maxDelay: TimeSpan.FromMinutes(1),
            jitterFactorMin: 0.9,
            jitterFactorMax: 0.9);

        var backoff = strategy.GetBackoff(1);

        // With jitter 0.9..0.9, first attempt should be very close to 9s
        Assert.True(backoff >= TimeSpan.FromSeconds(8.8));
        Assert.True(backoff <= TimeSpan.FromSeconds(9.2));
    }

    [Fact]
    public void GetBackoff_DeterministicWithFixedJitter_ProducesConsistentPattern()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromMinutes(1),
            jitterFactorMin: 0.95,
            jitterFactorMax: 0.95);

        var backoffs = new List<TimeSpan>();
        for (int i = 1; i <= 5; i++)
        {
            backoffs.Add(strategy.GetBackoff(i));
        }

        // With deterministic jitter 0.95, delays should be strictly increasing until cap
        Assert.True(backoffs[0] < backoffs[1]);
        Assert.True(backoffs[1] < backoffs[2]);
        Assert.True(backoffs[2] < backoffs[3]);
        Assert.True(backoffs[3] < backoffs[4]);
    }

    [Fact]
    public void GetBackoff_ZeroAttempts_ReturnsSameRange_AsFirstAttempt()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(2),
            maxDelay: TimeSpan.FromMinutes(5));

        var backoff0 = strategy.GetBackoff(0);
        var backoffNeg = strategy.GetBackoff(-5);

        // Both should be in range of first attempt (base * jitter)
        Assert.True(backoff0 >= TimeSpan.FromSeconds(0.9));
        Assert.True(backoff0 <= TimeSpan.FromSeconds(2.1));
        Assert.True(backoffNeg >= TimeSpan.FromSeconds(0.9));
        Assert.True(backoffNeg <= TimeSpan.FromSeconds(2.1));
    }

    [Fact]
    public void GetBackoff_VeryLargeAttemptNumber_CappedAtMaxDelay()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(5));

        var backoff = strategy.GetBackoff(100);

        // Even at attempt 100, should never exceed maxDelay
        Assert.True(backoff <= TimeSpan.FromMilliseconds(5100));
        Assert.True(backoff > TimeSpan.Zero);
    }

    [Fact]
    public void Constructor_JitterMaxLessThanMin_SwapsCorrectly()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            jitterFactorMin: 0.8,
            jitterFactorMax: 0.3);

        var backoff = strategy.GetBackoff(1);
        Assert.True(backoff > TimeSpan.Zero);
    }

    [Fact]
    public void Constructor_ZeroBaseDelay_UsesDefaultFiveSeconds()
    {
        var strategy = new ExponentialBackoffRetryStrategy(
            baseDelay: TimeSpan.Zero,
            maxDelay: TimeSpan.Zero);

        var backoff = strategy.GetBackoff(1);

        // Default base is 5s, so with jitter 0.5..1.0 → 2.5s..5s
        Assert.True(backoff >= TimeSpan.FromSeconds(2.4));
        Assert.True(backoff <= TimeSpan.FromSeconds(5.1));
    }

    [Fact]
    public void Shared_Instance_ProducesValidBackoffs()
    {
        for (int i = 1; i <= 10; i++)
        {
            var backoff = ExponentialBackoffRetryStrategy.Shared.GetBackoff(i);
            Assert.True(backoff > TimeSpan.Zero);
        }
    }
}
