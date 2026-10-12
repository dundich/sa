using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

public class FixedIntervalWaitStrategyTests
{
    [Fact]
    public void GetNextWait_AlwaysReturnsBaseInterval()
    {
        var strategy = FixedIntervalWaitStrategy.Shared;
        var baseInterval = TimeSpan.FromSeconds(3);

        Assert.Equal(baseInterval, strategy.GetNextWait(baseInterval, TimeSpan.FromMinutes(5), 1));
        Assert.Equal(baseInterval, strategy.GetNextWait(baseInterval, TimeSpan.FromMinutes(5), 100));
        Assert.Equal(baseInterval, strategy.GetNextWait(baseInterval, TimeSpan.Zero, 100));
    }
}

public class ExponentialIdleWaitStrategyTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Fact]
    public void GetNextWait_DisabledWhenMaxNotGreaterThanBase()
    {
        var strategy = new ExponentialIdleWaitStrategy();

        Assert.Equal(Base, strategy.GetNextWait(Base, TimeSpan.Zero, 5));
        Assert.Equal(Base, strategy.GetNextWait(Base, Base, 5));
        Assert.Equal(Base, strategy.GetNextWait(Base, Base - TimeSpan.FromMilliseconds(1), 5));
    }

    [Fact]
    public void GetNextWait_FirstEmptyPoll_KeepsBaseInterval()
    {
        var strategy = new ExponentialIdleWaitStrategy(multiplier: 2.0, jitter: 0.0);

        Assert.Equal(Base, strategy.GetNextWait(Base, Max, 1));
    }

    [Fact]
    public void GetNextWait_GrowsExponentially()
    {
        var strategy = new ExponentialIdleWaitStrategy(multiplier: 2.0, jitter: 0.0);

        Assert.Equal(TimeSpan.FromSeconds(1), strategy.GetNextWait(Base, Max, 1));
        Assert.Equal(TimeSpan.FromSeconds(2), strategy.GetNextWait(Base, Max, 2));
        Assert.Equal(TimeSpan.FromSeconds(4), strategy.GetNextWait(Base, Max, 3));
        Assert.Equal(TimeSpan.FromSeconds(8), strategy.GetNextWait(Base, Max, 4));
    }

    [Fact]
    public void GetNextWait_CappedAtMaxInterval()
    {
        var strategy = new ExponentialIdleWaitStrategy(multiplier: 2.0, jitter: 0.0);

        Assert.Equal(Max, strategy.GetNextWait(Base, Max, 100));
    }

    [Fact]
    public void GetNextWait_JitterNeverBelowBaseOrAboveMax()
    {
        var strategy = new ExponentialIdleWaitStrategy(multiplier: 2.0, jitter: 0.5);

        for (int n = 1; n <= 20; n++)
        {
            var wait = strategy.GetNextWait(Base, Max, n);
            Assert.True(wait >= Base, $"wait {wait} below base at n={n}");
            Assert.True(wait <= Max, $"wait {wait} above max at n={n}");
        }
    }

    [Fact]
    public void GetNextWait_ZeroBase_ReturnsZero()
    {
        var strategy = new ExponentialIdleWaitStrategy();

        Assert.Equal(TimeSpan.Zero, strategy.GetNextWait(TimeSpan.Zero, Max, 5));
    }
}

public class OutboxIdleStateTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(32);

    private static OutboxIdleState CreateState()
        => new(new ExponentialIdleWaitStrategy(multiplier: 2.0, jitter: 0.0), Base, Max);

    [Fact]
    public void CurrentInterval_StartsAtBase()
    {
        Assert.Equal(Base, CreateState().CurrentInterval);
    }

    [Fact]
    public void Report_Work_ResetsToBase()
    {
        var state = CreateState();

        state.Report(0);
        state.Report(0);
        Assert.Equal(TimeSpan.FromSeconds(4), state.CurrentInterval);

        state.Report(3);
        Assert.Equal(Base, state.CurrentInterval);
        Assert.Equal(0, state.ConsecutiveEmptyPolls);
    }

    [Fact]
    public void Report_Empty_BacksOffTowardsMax()
    {
        var state = CreateState();

        state.Report(0);
        Assert.Equal(Base, state.CurrentInterval); // n = 1
        state.Report(0);
        Assert.Equal(TimeSpan.FromSeconds(4), state.CurrentInterval); // n = 2
        state.Report(0);
        Assert.Equal(TimeSpan.FromSeconds(8), state.CurrentInterval); // n = 3
    }

    [Fact]
    public void Reset_ReturnsToBase()
    {
        var state = CreateState();

        state.Report(0);
        state.Report(0);
        state.Reset();

        Assert.Equal(Base, state.CurrentInterval);
        Assert.Equal(0, state.ConsecutiveEmptyPolls);
    }

    [Fact]
    public void AdaptiveIdleTiming_TracksCurrentInterval()
    {
        var state = CreateState();
        var timing = new AdaptiveIdleTiming(state);
        var now = DateTimeOffset.UnixEpoch;

        Assert.Equal(now + Base, timing.GetNextOccurrence(now));

        state.Report(0); // n = 1 → base
        Assert.Equal(now + Base, timing.GetNextOccurrence(now));

        state.Report(0); // n = 2 → 4s
        Assert.Equal(now + TimeSpan.FromSeconds(4), timing.GetNextOccurrence(now));

        state.Report(1); // work → base
        Assert.Equal(now + Base, timing.GetNextOccurrence(now));
    }
}

public class OutboxIdleStateRegistryTests
{
    [Fact]
    public void GetOrAdd_ReturnsSameInstanceForSameGroup()
    {
        var registry = new OutboxIdleStateRegistry(new FixedIntervalWaitStrategy());
        var baseInterval = TimeSpan.FromSeconds(1);

        var first = registry.GetOrAdd("group-a", baseInterval, TimeSpan.Zero);
        var second = registry.GetOrAdd("group-a", baseInterval, TimeSpan.Zero);

        Assert.Same(first, second);
    }

    [Fact]
    public void ResetAll_ResetsEveryGroup()
    {
        var registry = new OutboxIdleStateRegistry(new ExponentialIdleWaitStrategy(jitter: 0.0));
        var max = TimeSpan.FromSeconds(30);

        var a = registry.GetOrAdd("group-a", TimeSpan.FromSeconds(1), max);
        var b = registry.GetOrAdd("group-b", TimeSpan.FromSeconds(1), max);

        a.Report(0);
        a.Report(0);
        b.Report(0);
        b.Report(0);

        registry.ResetAll();

        Assert.Equal(TimeSpan.FromSeconds(1), a.CurrentInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), b.CurrentInterval);
    }

    [Fact]
    public void Get_UnknownGroup_ReturnsNull()
    {
        var registry = new OutboxIdleStateRegistry(new FixedIntervalWaitStrategy());

        Assert.Null(registry.Get("missing"));
    }
}

public class IdleBackoffSettingsTests
{
    [Fact]
    public void Build_Default_DisablesIdleBackoff()
    {
        var settings = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("g")
            .Build();

        Assert.Equal(TimeSpan.Zero, settings.IdleMaxInterval);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void WithIdleBackoff_SetsIdleMaxInterval()
    {
        var max = TimeSpan.FromMinutes(30);

        var settings = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("g")
            .WithIdleBackoff(max)
            .Build();

        Assert.Equal(max, settings.IdleMaxInterval);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void WithIdleBackoff_NonPositive_Throws()
    {
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("g");

        Assert.Throws<ArgumentException>(() => builder.WithIdleBackoff(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => builder.WithIdleBackoff(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void WithFixedInterval_ResetsToZero()
    {
        var settings = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("g")
            .WithIdleBackoff(TimeSpan.FromMinutes(5))
            .WithFixedInterval()
            .Build();

        Assert.Equal(TimeSpan.Zero, settings.IdleMaxInterval);
    }

    [Fact]
    public void Validate_IdleMaxBelowInterval_ReportsError()
    {
        var settings = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("g")
            .WithInterval(TimeSpan.FromMinutes(1))
            .WithIdleBackoff(TimeSpan.FromSeconds(30))
            .Build();

        Assert.Contains(settings.Validate(), e => e.Contains("IdleMaxInterval"));
    }

    [Fact]
    public void Validate_IdleMaxEqualInterval_IsValid()
    {
        var settings = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("g")
            .WithInterval(TimeSpan.FromMinutes(1))
            .WithIdleBackoff(TimeSpan.FromMinutes(1))
            .Build();

        Assert.DoesNotContain(settings.Validate(), e => e.Contains("IdleMaxInterval"));
    }
}

public class IdleWaitStrategyRegistrationTests
{
    private static IOutboxBuilder CreateBuilder(out ServiceCollection services)
    {
        services = new ServiceCollection();
        return OutboxBuilder.Create(services, OutboxScope.DefaultKey);
    }

    [Fact]
    public void Default_IsExponentialIdleWaitStrategy()
    {
        var builder = CreateBuilder(out var services);
        _ = builder;

        using var provider = services.BuildServiceProvider();

        Assert.IsType<ExponentialIdleWaitStrategy>(provider.GetRequiredService<IEmptyQueueWaitStrategy>());
    }

    [Fact]
    public void WithIdleWaitStrategy_Instance_OverridesDefault()
    {
        var builder = CreateBuilder(out var services);
        var custom = new ExponentialIdleWaitStrategy(multiplier: 1.5, jitter: 0.3);

        builder.WithIdleWaitStrategy(custom);

        using var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IEmptyQueueWaitStrategy>());
    }

    [Fact]
    public void WithIdleWaitStrategy_Type_RegistersType()
    {
        var builder = CreateBuilder(out var services);

        builder.WithIdleWaitStrategy<FixedIntervalWaitStrategy>();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FixedIntervalWaitStrategy>(provider.GetRequiredService<IEmptyQueueWaitStrategy>());
    }
}
