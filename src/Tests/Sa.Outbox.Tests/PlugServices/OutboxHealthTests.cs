using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.PlugServices;

/// <summary>
/// Unit tests of the aggregated provider health: per-provider checks, the no-check default, a
/// throwing check and the key/latency stamping done by the aggregate.
/// </summary>
public class OutboxHealthTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(
        params (string Key, IOutboxProviderHealth? Health)[] providers)
    {
        var services = new ServiceCollection();

        foreach ((string key, IOutboxProviderHealth? health) in providers)
        {
            services.AddSingleton(new OutboxProviderRegistration(
                key,
                new OutboxProviderDescriptor($"Provider:{key}"),
                HasDeliveryManager: true,
                HasTenantDetector: false));

            if (health is not null)
                services.AddKeyedSingleton<IOutboxProviderHealth>(key, health);
        }

        services.AddSingleton<IOutboxHealth, OutboxHealth>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task NoProviders_ReturnsAnEmptyReport()
    {
        using ServiceProvider sp = Build();

        IReadOnlyList<OutboxProviderHealthReport> reports =
            await sp.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task ProviderWithoutCheck_IsReportedHealthyWithoutAReason()
    {
        using ServiceProvider sp = Build(("a", null));

        OutboxProviderHealthReport report =
            Assert.Single(await sp.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken));

        Assert.Equal("a", report.ProviderKey);
        Assert.True(report.Healthy);
        Assert.Null(report.Reason);
    }

    [Fact]
    public async Task HealthyCheck_IsStampedWithTheProviderKey()
    {
        using ServiceProvider sp = Build(("a", new FixedHealth(new OutboxProviderHealthReport("", Healthy: true))));

        OutboxProviderHealthReport report =
            Assert.Single(await sp.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken));

        Assert.Equal("a", report.ProviderKey);
        Assert.True(report.Healthy);
    }

    [Fact]
    public async Task UnhealthyReport_KeepsItsReason()
    {
        using ServiceProvider sp = Build(
            ("a", new FixedHealth(new OutboxProviderHealthReport("", false, "connection refused"))));

        OutboxProviderHealthReport report =
            Assert.Single(await sp.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken));

        Assert.False(report.Healthy);
        Assert.Equal("connection refused", report.Reason);
    }

    [Fact]
    public async Task ThrowingCheck_IsReportedUnhealthyInsteadOfFailingTheAggregate()
    {
        using ServiceProvider sp = Build(("a", new ThrowingHealth()));

        OutboxProviderHealthReport report =
            Assert.Single(await sp.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken));

        Assert.False(report.Healthy);
        Assert.Contains("boom", report.Reason!);
    }

    [Fact]
    public async Task Reports_AreOrderedByProviderKey()
    {
        using ServiceProvider sp = Build(
            ("zeta", null),
            ("alpha", null),
            ("mike", null));

        IReadOnlyList<OutboxProviderHealthReport> reports =
            await sp.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken);

        Assert.Equal(new[] { "alpha", "mike", "zeta" }, reports.Select(r => r.ProviderKey));
    }

    private sealed class FixedHealth(OutboxProviderHealthReport report) : IOutboxProviderHealth
    {
        public ValueTask<OutboxProviderHealthReport> CheckAsync(CancellationToken cancellationToken = default)
            => new(report);
    }

    private sealed class ThrowingHealth : IOutboxProviderHealth
    {
        public ValueTask<OutboxProviderHealthReport> CheckAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
