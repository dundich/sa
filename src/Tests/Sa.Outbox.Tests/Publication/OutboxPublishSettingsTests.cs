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
