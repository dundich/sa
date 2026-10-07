using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorageTests;

/// <summary>
/// The chain of a basket: registration order by default, the configured storage-type order on top
/// (a global list with a per-basket override), plus the registration-order contract itself.
/// </summary>
public sealed class OrderingTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    // ---------- registration order: the documented contract ----------

    [Fact]
    public void Storages_FollowTheOrderOfTheRegistrationCalls()
    {
        // The in-memory provider registered through the pipeline is added when the hybrid
        // registration runs — its position in the chain is the position of that call.
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("svc-first"))
            .AddSaHybridFileStorage(b => b.AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("pipeline-second")));

        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<IHybridFileStorage>();

        Assert.Equal(
            new[] { "svc-first", "pipeline-second" },
            hybrid.Storages.Select(s => s.Basket).ToArray());
    }

    [Fact]
    public void Storages_ConfigureStorageAddsGoAfterTheServiceCollectionOnes()
    {
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("di-first"))
            .AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) =>
                c.AddStorage(new InMemoryFileStorage(new InMemoryFileStorageOptions("configured-second")))));

        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<IHybridFileStorage>();

        Assert.Equal(
            new[] { "di-first", "configured-second" },
            hybrid.Storages.Select(s => s.Basket).ToArray());
    }

    [Fact]
    public void Storages_PipelineInMemory_TakesThePositionOfTheAddSaHybridFileStorageCall()
    {
        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b.AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("pipeline-first")))
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("svc-second"));

        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<IHybridFileStorage>();

        Assert.Equal(
            new[] { "pipeline-first", "svc-second" },
            hybrid.Storages.Select(s => s.Basket).ToArray());
    }

    // ---------- configured type order ----------

    [Fact]
    public async Task OrderTypes_NotConfigured_KeepsTheRegistrationOrder()
    {
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, static _ => { });

        var result = await UploadAsync(provider);

        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.Equal(0, fake.UploadCount);
    }

    [Fact]
    public async Task OrderTypes_ReordersTheChain()
    {
        // mem is registered first, "fake" second — the configured order puts fake first.
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b.OrderTypes("fake", "mem"));

        var result = await UploadAsync(provider);

        Assert.Equal("fake", result.StorageType);
        Assert.Equal(1, fake.UploadCount);
    }

    [Fact]
    public async Task OrderTypes_EmptyList_KeepsTheRegistrationOrder()
    {
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b.OrderTypes());

        var result = await UploadAsync(provider);

        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.Equal(0, fake.UploadCount);
    }

    [Fact]
    public async Task OrderTypes_ListedTypeWithoutAStorage_IsSkippedSilently()
    {
        // "fs" is listed but nothing was registered under it — the chain still holds "mem".
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b.OrderTypes("fs", "mem"));

        var result = await UploadAsync(provider);

        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.Equal(0, fake.UploadCount);
    }

    [Fact]
    public async Task OrderTypes_ExcludesRegisteredTypesThatAreNotListed()
    {
        // mem is registered and writable, but "mem" is not in the list: the chain holds only
        // the read-only fake, so nothing is writable — the excluded type cannot be used.
        var fake = new FakeFileStorage("share", "fake", isReadOnly: true);
        using var provider = BuildProvider(fake, b => b.OrderTypes("fake"));

        await Assert.ThrowsAsync<HybridFileStorageWritableException>(
            () => UploadAsync(provider));
    }

    [Fact]
    public async Task OrderTypes_OnlyUnlistedTypesLeft_LeavesTheChainEmpty()
    {
        // The one listed type ("pg") has no registered storage — nothing to write through.
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b.OrderTypes("pg"));

        await Assert.ThrowsAsync<HybridFileStorageNoAvailableException>(
            () => UploadAsync(provider));
    }

    [Fact]
    public async Task OrderTypes_ExcludedType_IsNotProbedOnDownload()
    {
        // A "mem://" file in a basket whose order does not list "mem": the recognizing storage
        // is excluded, so there are no candidates — NoAvailable rather than a probed `false`.
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b.OrderTypes("fs"));

        await Assert.ThrowsAsync<HybridFileStorageNoAvailableException>(
            () => provider.GetRequiredService<IHybridFileStorage>().DownloadAsync(
                "mem://share/1/missing.txt",
                static (_, _) => Task.CompletedTask,
                TestToken));
    }

    [Fact]
    public async Task OrderTypes_WithinOneType_KeepsTheRegistrationOrder()
    {
        var first = new FakeFileStorage("share", "fake");
        var second = new FakeFileStorage("share", "fake");

        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b
                .ConfigureStorage((_, c) => c.AddStorage(first).AddStorage(second))
                .OrderTypes("fake"));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        var result = await UploadAsync(storage);

        Assert.Equal("fake", result.StorageType);
        Assert.Equal(1, first.UploadCount);
        Assert.Equal(0, second.UploadCount);
    }

    // ---------- per-basket override ----------

    [Fact]
    public async Task OrderBasketTypes_OverridesTheGlobalOrderForThatBasket()
    {
        // The global order puts fake first; the basket override puts mem first again.
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b
            .OrderTypes("fake", "mem")
            .OrderBasketTypes("share", "mem", "fake"));

        var result = await UploadAsync(provider);

        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.Equal(0, fake.UploadCount);
    }

    [Fact]
    public async Task OrderBasketTypes_EmptyOverride_FallsBackToTheGlobalOrder()
    {
        var fake = new FakeFileStorage("share", "fake");
        using var provider = BuildProvider(fake, b => b
            .OrderTypes("fake", "mem")
            .OrderBasketTypes("share"));

        var result = await UploadAsync(provider);

        Assert.Equal("fake", result.StorageType);
        Assert.Equal(1, fake.UploadCount);
    }

    /// <summary>
    /// One in-memory storage (registered first) plus the given fake (registered second) in the
    /// basket "share" — the pair every order test contrasts.
    /// </summary>
    private static ServiceProvider BuildProvider(
        FakeFileStorage fake,
        Action<IHybridFileStorageConfiguration> configure)
        => new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("share"))
            .AddSaHybridFileStorage(b =>
            {
                b.ConfigureStorage((_, c) => c.AddStorage(fake));
                configure(b);
            })
            .BuildServiceProvider();

    private static Task<StorageResult> UploadAsync(ServiceProvider provider)
        => UploadAsync(provider.GetRequiredService<IHybridFileStorage>());

    private static async Task<StorageResult> UploadAsync(IHybridFileStorage storage)
    {
        using var stream = FixtureHelper.GetByteStream();
        return await storage.UploadAsync(
            "share",
            new UploadFileInput { FileName = "order.txt", TenantId = 1 },
            stream,
            TestToken);
    }
}
