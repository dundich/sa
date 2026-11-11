using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.Partitional;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.Partitional;

/// <summary>
/// Per-provider tenants: a keyed <see cref="TenantSettings"/> wins over the shared fallback, and a
/// provider's own keyed detector is used for that provider's auto-detection.
/// </summary>
public class TenantProviderPerProviderTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(
        TenantSettings? shared = null,
        params (string Key, TenantSettings Settings)[] providers)
    {
        var services = new ServiceCollection();

        services.AddSingleton(shared ?? new TenantSettings());

        foreach ((string key, TenantSettings settings) in providers)
            services.AddKeyedSingleton(key, settings);

        services.AddSingleton<IOutboxStorage, OutboxStorage>();
        services.AddSingleton<ITenantProvider, TenantProvider>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task KeyedSettings_WinOverTheSharedFallback()
    {
        var shared = new TenantSettings();
        shared.WithTenantIds(9);

        var a = new TenantSettings();
        a.WithTenantIds(1, 2);

        using ServiceProvider sp = Build(shared, ("a", a));

        var provider = sp.GetRequiredService<ITenantProvider>();

        Assert.Equal(new[] { 1, 2 }, await provider.GetTenantIdsAsync("a", TestToken));
        Assert.Equal(new[] { 9 }, await provider.GetTenantIdsAsync("b", TestToken));
    }

    [Fact]
    public async Task ProviderWithoutItsOwnSettings_UsesTheSharedFallback()
    {
        var shared = new TenantSettings();
        shared.WithTenantIds(9);

        using ServiceProvider sp = Build(shared);

        var provider = sp.GetRequiredService<ITenantProvider>();

        Assert.Equal(new[] { 9 }, await provider.GetTenantIdsAsync("any", TestToken));
    }

    [Fact]
    public async Task KeyedDetector_IsUsedForItsOwnProvider()
    {
        var settingsA = new TenantSettings();
        settingsA.WithAutoDetect();

        var settingsB = new TenantSettings();
        settingsB.WithAutoDetect();

        var services = new ServiceCollection();
        services.AddSingleton(new TenantSettings());
        services.AddKeyedSingleton("a", settingsA);
        services.AddKeyedSingleton("b", settingsB);
        services.AddKeyedSingleton<IOutboxTenantDetector>("a", new FakeDetector(1, 2));
        services.AddKeyedSingleton<IOutboxTenantDetector>("b", new FakeDetector(7));
        services.AddSingleton<IOutboxStorage, OutboxStorage>();
        services.AddSingleton<ITenantProvider, TenantProvider>();

        using ServiceProvider sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<ITenantProvider>();

        Assert.Equal(new[] { 1, 2 }, await provider.GetTenantIdsAsync("a", TestToken));
        Assert.Equal(new[] { 7 }, await provider.GetTenantIdsAsync("b", TestToken));
    }

    private sealed class FakeDetector(params int[] detected) : IOutboxTenantDetector
    {
        public bool CanDetect => true;

        public ValueTask<int[]> GetTenantIdsAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(detected);
    }
}
