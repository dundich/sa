using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.PlugServices;

/// <summary>
/// Unit tests of the provider plug point: <c>AddOutboxProvider</c> registration, the
/// <see cref="IOutboxProviderRegistry"/> validation rules and the startup validator.
/// </summary>
public class OutboxProviderSetupTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static OutboxProviderDescriptor Descriptor(
        OutboxProviderCapabilities capabilities = OutboxProviderCapabilities.None,
        string name = "TestProvider")
        => new(name, capabilities);

    #region Registration

    [Fact]
    public void AddOutboxProvider_RegistersDescriptorWriterAndRegistry()
    {
        var services = new ServiceCollection();

        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        IOutboxProviderRegistry registry = sp.GetRequiredService<IOutboxProviderRegistry>();
        Assert.Equal("TestProvider", registry.Descriptor.Name);
        Assert.NotNull(sp.GetService<IOutboxBulkWriter>());
        Assert.Single(sp.GetServices<OutboxProviderRegistration>());
    }

    [Fact]
    public void AddOutboxProvider_CapabilitiesRoundTrip()
    {
        var services = new ServiceCollection();
        const OutboxProviderCapabilities capabilities =
            OutboxProviderCapabilities.PublishContext | OutboxProviderCapabilities.Postponement;

        services.AddOutboxProvider(
            Descriptor(capabilities),
            provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        OutboxProviderDescriptor descriptor = sp.GetRequiredService<IOutboxProviderRegistry>().Descriptor;
        Assert.Equal(capabilities, descriptor.Capabilities);
        Assert.True(descriptor.Supports(OutboxProviderCapabilities.PublishContext));
        Assert.False(descriptor.Supports(OutboxProviderCapabilities.TenantDetection));
    }

    [Fact]
    public void AddOutboxProvider_Version_DefaultsToTheWriterAssembly()
    {
        var services = new ServiceCollection();

        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        string? expected = typeof(FakeWriter).Assembly.GetName().Version?.ToString();
        Assert.Equal(expected, sp.GetRequiredService<IOutboxProviderRegistry>().Descriptor.Version);
    }

    [Fact]
    public void AddOutboxProvider_ExplicitVersion_Wins()
    {
        var services = new ServiceCollection();

        services.AddOutboxProvider(
            new OutboxProviderDescriptor("TestProvider", Version: "1.2.3"),
            provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Equal("1.2.3", sp.GetRequiredService<IOutboxProviderRegistry>().Descriptor.Version);
    }

    [Fact]
    public void AddOutboxProvider_BlankName_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(
            () => services.AddOutboxProvider(new OutboxProviderDescriptor("  "), _ => { }));
    }

    [Fact]
    public void AddOutboxProvider_WithoutBulkWriter_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => services.AddOutboxProvider(Descriptor(), _ => { }));
    }

    [Fact]
    public void AddOutboxProvider_SecondDifferentProvider_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        Assert.Throws<InvalidOperationException>(() => services.AddOutboxProvider(
            Descriptor(name: "AnotherProvider"),
            provider => provider.WithBulkWriter<FakeWriter>()));
    }

    [Fact]
    public void AddOutboxProvider_SameProviderTwice_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Single(sp.GetServices<OutboxProviderRegistration>());
        Assert.Equal("TestProvider", sp.GetRequiredService<IOutboxProviderRegistry>().Descriptor.Name);
    }

    #endregion

    #region Registry validation

    [Fact]
    public void Registry_NoProviderRegistered_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxProviderRegistry, OutboxProviderRegistry>();

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IOutboxProviderRegistry>());
    }

    [Fact]
    public void TamperedBulkWriterRegistration_IsNotValidatedByConstruction_ButFailsOnResolve()
    {
        // The registry validates the registration marker and never constructs provider services
        // (building a storage chain can open connections while job settings are built). Removing a
        // registration afterwards is a container-level misuse that DI reports on first resolve.
        var services = new ServiceCollection();
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());
        services.RemoveAllKeyed<IOutboxBulkWriter>(Sa.Outbox.OutboxScope.DefaultKey);

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IOutboxProviderRegistry>());
        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IOutboxBulkWriter>());
    }

    [Fact]
    public void Registry_MissingDeliveryManager_WithoutConsumerGroups_Passes()
    {
        // A publish-only deployment: no consumer groups, so no delivery manager is required.
        var services = new ServiceCollection();
        services.AddSaOutbox();
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Equal("TestProvider", sp.GetRequiredService<IOutboxProviderRegistry>().Descriptor.Name);
    }

    [Fact]
    public void Registry_MissingDeliveryManager_WithConsumerGroups_Throws()
    {
        var services = new ServiceCollection();
        services.AddSaOutbox(builder => builder.WithDeliveries(deliveries => deliveries
            .AddDelivery<FakeConsumer, FakeMessage>("grp")));
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IOutboxProviderRegistry>());
    }

    [Fact]
    public void Registry_DeliveryManagerPresent_WithConsumerGroups_Passes()
    {
        var services = new ServiceCollection();
        services.AddSaOutbox(builder => builder.WithDeliveries(deliveries => deliveries
            .AddDelivery<FakeConsumer, FakeMessage>("grp")));
        services.AddOutboxProvider(
            Descriptor(),
            provider => provider
                .WithBulkWriter<FakeWriter>()
                .WithDeliveryManager<FakeDeliveryManager>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IOutboxProviderRegistry>());
    }

    [Fact]
    public void Registry_TenantDetectionCapability_WithoutDetector_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutboxProvider(
            Descriptor(OutboxProviderCapabilities.TenantDetection),
            provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IOutboxProviderRegistry>());
    }

    [Fact]
    public void Registry_TenantDetectionCapability_WithDetector_Passes()
    {
        var services = new ServiceCollection();
        services.AddOutboxProvider(
            Descriptor(OutboxProviderCapabilities.TenantDetection),
            provider => provider
                .WithBulkWriter<FakeWriter>()
                .WithTenantDetector<FakeDetector>());

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IOutboxProviderRegistry>());
    }

    #endregion

    #region Startup validator

    [Fact]
    public async Task HostedValidator_IsRegistered_AndStarts()
    {
        var services = new ServiceCollection();
        services.AddOutboxProvider(Descriptor(), provider => provider.WithBulkWriter<FakeWriter>());

        using ServiceProvider sp = services.BuildServiceProvider();

        OutboxProviderValidationHostedService validator = Assert.Single(
            sp.GetServices<IHostedService>().OfType<OutboxProviderValidationHostedService>());

        await validator.StartAsync(TestToken);
        await validator.StopAsync(TestToken);
    }

    #endregion

    #region Fakes

    internal sealed class FakeMessage
    {
        public int TenantId { get; set; }
    }

    internal sealed class FakeConsumer : IConsumer<FakeMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<FakeMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class FakeWriter : IOutboxBulkWriter
    {
        public ValueTask<ulong> InsertBulk<TMessage>(
            OutboxMessageBatch<TMessage> batch,
            object? context,
            CancellationToken cancellationToken = default)
            => new((ulong)batch.Count);
    }

    internal sealed class FakeDeliveryManager : IOutboxDeliveryManager
    {
        public Task<int> RentDelivery<TMessage>(
            Memory<IOutboxContextOperations<TMessage>> writeBuffer,
            TimeSpan lockDuration,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> ReturnDelivery<TMessage>(
            ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => Task.FromResult(messages.Length);

        public Task<int> ExtendDelivery(
            TimeSpan lockExpiration,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => Task.FromResult(0);
    }

    internal sealed class FakeDetector : IOutboxTenantDetector
    {
        public bool CanDetect => true;

        public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
            => new([1]);
    }

    #endregion
}
