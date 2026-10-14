using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// The base <see cref="NoopDeliveryGroupOptionsProvider"/>: with the plain <c>Sa.Outbox</c>
/// package a consumer group is shaped purely by <see cref="OutboxDefaults"/> and the user
/// <c>configure</c> callback — the provider must never touch the builder, whatever key it is
/// asked with.
/// </summary>
public class NoopDeliveryGroupOptionsProviderTests
{
    [Fact]
    public void TryApply_ReturnsFalse_AndLeavesBuilderUntouched()
    {
        var provider = new NoopDeliveryGroupOptionsProvider();
        var builder = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("grp")
            .WithInterval(TimeSpan.FromMilliseconds(100));

        bool applied = provider.TryApply("grp", "some:config:key", builder);

        Assert.False(applied);

        // The builder is untouched: its value is preserved, everything else stays at defaults.
        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(TimeSpan.FromMilliseconds(100), settings.Interval);
        Assert.Equal(OutboxDefaults.MaxBatchSize, settings.MaxBatchSize);
        Assert.Equal(OutboxDefaults.LookbackInterval, settings.LookbackInterval);
    }

    [Fact]
    public void TryApply_NullOrEmptyKey_ReturnsFalse()
    {
        var provider = new NoopDeliveryGroupOptionsProvider();
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.False(provider.TryApply("grp", null, builder));
        Assert.False(provider.TryApply("grp", string.Empty, builder));
        Assert.False(provider.TryApply("grp", "   ", builder));
    }

    [Fact]
    public void DefaultRegistration_ResolvesNoopProvider()
    {
        var services = new ServiceCollection();

        services.AddDefaultDeliveryGroupOptionsProvider();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<NoopDeliveryGroupOptionsProvider>(sp.GetRequiredService<IDeliveryGroupOptionsProvider>());
    }

    [Fact]
    public void DefaultRegistration_IsTryAdd_DoesNotOverrideExistingProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IDeliveryGroupOptionsProvider, CustomProvider>();
        services.AddDefaultDeliveryGroupOptionsProvider();

        using var sp = services.BuildServiceProvider();

        // The house pattern is TryAdd: a package registered before the base default wins.
        Assert.IsType<CustomProvider>(sp.GetRequiredService<IDeliveryGroupOptionsProvider>());
    }

    private sealed class CustomProvider : IDeliveryGroupOptionsProvider
    {
        public bool TryApply(string consumerGroupId, string? configurationKey, OutboxConsumerSettingsBuilder builder) => true;
    }
}
