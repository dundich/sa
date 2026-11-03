using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Publication;

namespace Sa.Outbox.Tests.Publication;

/// <summary>
/// Publish settings are immutable, so <c>WithPublishSettings</c> takes the current settings and
/// returns the updated ones; the registration is keyed by the outbox scope, like every other
/// provider-local setting.
/// </summary>
public class OutboxPublishSettingsTests
{
    [Fact]
    public void WithPublishSettings_AppliesTheReturnedSettings()
    {
        var services = new ServiceCollection();

        services.AddSaOutbox(
            builder => builder.WithPublishSettings((_, settings) => settings.WithMaxBatchSize(1024)));

        using ServiceProvider provider = services.BuildServiceProvider();

        // The default key keeps its unkeyed alias.
        Assert.Equal(1024, provider.GetRequiredService<OutboxPublishSettings>().MaxBatchSize);
    }

    [Fact]
    public void RepeatedRegistration_AppliesTheCallbacksOnce()
    {
        var services = new ServiceCollection();
        int calls = 0;

        services.AddSaOutbox(Configure, scopeKey: "dup");
        services.AddSaOutbox(Configure, scopeKey: "dup");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(8, provider.GetRequiredKeyedService<OutboxPublishSettings>("dup").MaxBatchSize);

        // The factory applies every callback of the scope, so a repeated AddSaOutbox for the same
        // scope replaces the previous registration's callbacks instead of running them twice.
        Assert.Equal(1, calls);

        void Configure(IOutboxBuilder builder) => builder.WithPublishSettings((_, settings) =>
        {
            calls++;
            return settings.WithMaxBatchSize(8);
        });
    }

    [Fact]
    public void PublishSettings_ArePerProviderKey()
    {
        var services = new ServiceCollection();

        services.AddSaOutbox(
            builder => builder.WithPublishSettings((_, settings) => settings.WithMaxBatchSize(1024)),
            scopeKey: "big");

        services.AddSaOutbox(
            builder => builder.WithPublishSettings((_, settings) => settings.WithMaxBatchSize(32)),
            scopeKey: "small");

        services.AddSaOutbox(scopeKey: "defaults");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(1024, provider.GetRequiredKeyedService<OutboxPublishSettings>("big").MaxBatchSize);
        Assert.Equal(32, provider.GetRequiredKeyedService<OutboxPublishSettings>("small").MaxBatchSize);
        Assert.Equal(
            new OutboxPublishSettings().MaxBatchSize,
            provider.GetRequiredKeyedService<OutboxPublishSettings>("defaults").MaxBatchSize);
    }
}
