using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.InMemory;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.InMemory;

/// <summary>
/// Registration-level checks of the in-memory plug point: the contracts, the descriptor and the
/// honest capability set. Nothing is delivered here.
/// </summary>
public class InMemoryRegistrationTests
{
    [Fact]
    public void AddSaOutboxUsingInMemory_RegistersAllThreeContracts()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingInMemory();

        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxBulkWriter));
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxDeliveryManager));
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxTenantDetector));
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxProviderRegistry));
    }

    [Fact]
    public void AddSaOutboxUsingInMemory_DeclaresOnlyHonestCapabilities()
    {
        using ServiceProvider provider = InMemoryTestHost.Build();

        OutboxProviderDescriptor descriptor = provider
            .GetRequiredService<IOutboxProviderRegistry>()
            .Descriptor;

        Assert.Equal("InMemory", descriptor.Name);
        Assert.True(descriptor.Supports(OutboxProviderCapabilities.TenantDetection));
        Assert.True(descriptor.Supports(OutboxProviderCapabilities.LockExtension));
        Assert.True(descriptor.Supports(OutboxProviderCapabilities.Postponement));
        Assert.False(descriptor.Supports(OutboxProviderCapabilities.PublishContext));
    }

    [Fact]
    public void AddSaOutboxUsingInMemory_CustomScopeKey_KeepsProvidersIndependent()
    {
        using ServiceProvider provider = InMemoryTestHost.Build(scopeKey: "second");

        Assert.Equal(
            "InMemory",
            provider.GetRequiredKeyedService<IOutboxProviderRegistry>("second").Descriptor.Name);

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOutboxProviderRegistry>());
    }

    [Fact]
    public void AddSaOutboxUsingInMemory_Disabled_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingInMemory(settings => settings.Enabled = false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOutboxBulkWriter));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOutboxProviderRegistry));
    }

    [Fact]
    public void AddSaOutboxUsingInMemory_NegativeCapacity_IsRejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddSaOutboxUsingInMemory(settings => settings.Capacity = -1));
    }

    [Fact]
    public void AddSaOutboxUsingInMemory_ReservedScopeKey_IsRejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddSaOutboxUsingInMemory(scopeKey: "default"));
    }
}
