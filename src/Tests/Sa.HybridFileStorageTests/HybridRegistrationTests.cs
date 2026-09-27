using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorageTests;

/// <summary>
/// Registration-level behaviour of the hybrid entry points: a second call used to be silently
/// discarded, the in-memory provider used to be built outside the container, and an empty
/// container used to resolve successfully and then fail every operation.
/// </summary>
public sealed class HybridRegistrationTests
{
    // ---------- double registration ----------

    [Fact]
    public void AddSaHybridFileStorage_Throws_OnASecondCall()
    {
        var services = new ServiceCollection();
        services.AddSaInMemoryFileStorage();
        services.AddSaHybridFileStorage();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSaHybridFileStorage());

        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSaHybridFileStorage_Throws_WhenTheSecondCallWouldLoseStorages()
    {
        var services = new ServiceCollection();
        services.AddSaInMemoryFileStorage();
        services.AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) =>
            c.AddStorage(new InMemoryFileStorage(new InMemoryFileStorageOptions("extra")))));

        // The first registration is in place; the second builder's storages would never run.
        Assert.Throws<InvalidOperationException>(() => services.AddSaHybridFileStorage(b =>
            b.ConfigureStorage((_, c) =>
                c.AddStorage(new InMemoryFileStorage(new InMemoryFileStorageOptions("lost"))))));

        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<IHybridFileStorage>();

        // The surviving registration is the first one, and it is still usable.
        Assert.Contains(hybrid.Storages, s => s.Basket == "share");
        Assert.Contains(hybrid.Storages, s => s.Basket == "extra");
        Assert.DoesNotContain(hybrid.Storages, s => s.Basket == "lost");
    }

    [Fact]
    public void AddSaHybridFileStorage_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaHybridFileStorage());
    }

    [Fact]
    public void AddSaHybridFileStorage_Accepts_AnExternalIHybridFileStorageRegistration()
    {
        // The guard keys on IHybridFileStorage, not on this assembly's builder, so a hand-rolled
        // registration is detected too.
        var services = new ServiceCollection();
        services.AddSingleton<IHybridFileStorage>(_ => null!);

        Assert.Throws<InvalidOperationException>(() => services.AddSaHybridFileStorage());
    }

    // ---------- empty container ----------

    [Fact]
    public void Resolve_Throws_WhenNoStorageWasRegistered()
    {
        var services = new ServiceCollection();
        services.AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHybridFileStorage>());

        Assert.Contains("AddSaInMemoryFileStorage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Succeeds_WithAStorageAddedExplicitly()
    {
        var services = new ServiceCollection();
        services.AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) =>
            c.AddStorage(new InMemoryFileStorage(new InMemoryFileStorageOptions("direct")))));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IHybridFileStorage>());
    }

    // ---------- in-memory provider goes through the container ----------

    [Fact]
    public void AddSaInMemoryFileStorage_RegistersTheProviderInTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSaHybridFileStorage(b => b.AddSaInMemoryFileStorage());

        using var provider = services.BuildServiceProvider();

        // Previously the storage was built with `new` inside the deferred callback, so it was
        // invisible to sp.GetServices<IFileStorage>() and outside the provider's disposal.
        Assert.Single(provider.GetServices<IFileStorage>());
    }

    [Fact]
    public async Task AddSaInMemoryFileStorage_PutsTheSameInstanceInTheHybridContainer()
    {
        var services = new ServiceCollection();
        services.AddSaHybridFileStorage(b => b.AddSaInMemoryFileStorage(
            new InMemoryFileStorageOptions("pipeline")));

        using var provider = services.BuildServiceProvider();

        var fromDi = provider.GetServices<IFileStorage>().OfType<InMemoryFileStorage>().Single();
        var hybrid = provider.GetRequiredService<IHybridFileStorage>();

        // If a second instance were created, the upload would land in one store and a read would
        // look in the other. Reference equality is what ties the container to the DI instance.
        Assert.Same(fromDi, hybrid.Storages.Single());
        Assert.Equal("pipeline", fromDi.Basket);
    }

    [Fact]
    public void AddSaInMemoryFileStorage_CoexistsWithOtherProviders()
    {
        var services = new ServiceCollection();
        services.AddSaHybridFileStorage(b => b
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("memory"))
            .ConfigureStorage((_, c) =>
                c.AddStorage(new InMemoryFileStorage(new InMemoryFileStorageOptions("second")))));

        using var provider = services.BuildServiceProvider();

        // Only the pipeline provider is a container service; one added through ConfigureStorage
        // is the caller's own object and was never registered.
        Assert.Equal("memory", Assert.Single(provider.GetServices<IFileStorage>()).Basket);

        var hybrid = provider.GetRequiredService<IHybridFileStorage>();
        Assert.Equal(2, hybrid.Storages.Count());
        Assert.Contains(hybrid.Storages, s => s.Basket == "memory");
        Assert.Contains(hybrid.Storages, s => s.Basket == "second");
    }

    [Fact]
    public void AddSaInMemoryFileStorage_Rejects_NullConfiguration()
    {
        IHybridFileStorageConfiguration configuration = null!;

        Assert.Throws<ArgumentNullException>(() => configuration.AddSaInMemoryFileStorage());
    }

    [Fact]
    public void AddSaInMemoryFileStorage_Accepts_AnEmptyBasket()
    {
        // An empty basket is a supported configuration: it makes the file ID two segments deep
        // (scheme://tenant/file) and several tests rely on it. Validating the basket here would
        // break them, so it is deliberately left to the caller.
        var services = new ServiceCollection();
        services.AddSaHybridFileStorage(b =>
            b.AddSaInMemoryFileStorage(new InMemoryFileStorageOptions(string.Empty)));

        using var provider = services.BuildServiceProvider();

        var storage = provider.GetServices<IFileStorage>().Single();
        Assert.Equal(string.Empty, storage.Basket);
    }

    // ---------- TimeProvider ----------

    [Fact]
    public void AddSaInMemoryFileStorage_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddSaInMemoryFileStorage();

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void AddSaInMemoryFileStorage_RespectsAPreviouslyRegisteredTimeProvider()
    {
        var fake = new TimeProviderStub(DateTimeOffset.UnixEpoch);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSaHybridFileStorage(b => b.AddSaInMemoryFileStorage());

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task AddSaInMemoryFileStorage_UsesTheRegisteredTimeProvider()
    {
        var expected = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new TimeProviderStub(expected));
        services.AddSaHybridFileStorage(b => b.AddSaInMemoryFileStorage());

        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<IHybridFileStorage>();

        using var content = FixtureHelper.GetByteStream();
        var result = await hybrid.UploadAsync(
            StorageNaming.DefaultBasket,
            new UploadFileInput { FileName = "stamped.txt", TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.UploadedAt);
    }

    private sealed class TimeProviderStub(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
