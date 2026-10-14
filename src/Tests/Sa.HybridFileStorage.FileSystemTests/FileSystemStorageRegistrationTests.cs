using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Registration-level behaviour of the filesystem provider: the named-only keyed setup, the options
/// pipeline order, the mandatory <c>TempFolder</c> channel and the storage defaults that channel
/// carries — what the constructed storage receives.
/// </summary>
public sealed class FileSystemStorageRegistrationTests : IDisposable
{
    private const string Name = "reg";

    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"fs_reg_{Path.GetRandomFileName()}");

    public FileSystemStorageRegistrationTests() => Directory.CreateDirectory(_testDir);

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { /* best effort */ }
    }

    private static FileSystemStorageOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptionsMonitor<FileSystemStorageOptions>>()
            .Get(provider.GetRequiredKeyedService<FileSystemStorageRegistration>(Name).OptionsName);

    private static ITempFolder TempFolder(IServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>(Name);

    // ---------- null / argument checking ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(
            () => services.AddSaFileSystemFileStorage(Name, _ => { }));
    }

    [Fact]
    public void Register_Rejects_BlankName()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(
            () => services.AddSaFileSystemFileStorage("   ", _ => { }));
    }

    [Fact]
    public void Register_WithoutTempFolder_Throws()
    {
        // The root lives on TempFolderOptions; a storage without the channel has nowhere to put a
        // file, so the omission fails where it is visible rather than at first upload.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaFileSystemFileStorage(Name, b =>
                b.Options(ob => ob.Configure(x => x.Basket = "documents"))));

        Assert.Contains("TempFolder", ex.Message, StringComparison.Ordinal);
    }

    // ---------- pipeline order: configure -> post-configure -> validate ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        // TempFolderOptions.RootPath defaults to the system temp directory, which the filesystem
        // provider refuses as a storage root. Options are materialised lazily, so the failure
        // surfaces as OptionsValidationException when the storage is first resolved.
        var services = new ServiceCollection();
        services.AddFsStorageWithTempFolder(Name, _ => { });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("system temp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_WhitespaceRoot_IsRejected_NotNormalisedIntoADirectory()
    {
        // A blank RootPath must be rejected rather than silently resolved.
        var services = new ServiceCollection();
        services.AddFsStorageWithTempFolder(Name, tb =>
            tb.Options(ob => ob.Configure(x => x.RootPath = "   ")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_ResolvesTheRootToAFullPath()
    {
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, Name);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(Path.GetFullPath(_testDir), TempFolder(provider).RootPath);
    }

    [Fact]
    public void Register_PostConfigures_TrimsTheNames()
    {
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, Name, b => b.Options(ob => ob.Configure(x =>
        {
            x.StorageType = "  fsx  ";
            x.Basket = "  documents  ";
        })));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal("fsx", options.StorageType);
        Assert.Equal("documents", options.Basket);
    }

    [Fact]
    public void Register_PostConfiguresBeforeValidating()
    {
        // A basket that is only invalid because of surrounding whitespace must pass once trimmed —
        // proof that validation runs after post-configuration, not before it.
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, Name, b => b.Options(ob => ob.Configure(x =>
            x.Basket = "  documents  ")));

        using var provider = services.BuildServiceProvider();

        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", storage.Basket);
    }

    // ---------- the builder handed to the callback ----------

    [Fact]
    public void Register_CallbackCanAddAPostConfigure()
    {
        // Pre-initialisation is Configure, post-initialisation is PostConfigure: both are available
        // on the OptionsBuilder the callback receives.
        var services = new ServiceCollection();

        services.AddFsStorage(_testDir, Name, b => b
            .Options(ob => ob.Configure(x => x.Basket = "documents")
                .PostConfigure(x => x.IsReadOnly = true)));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.True(options.IsReadOnly);
        Assert.Equal("documents", options.Basket);
    }

    [Fact]
    public void Register_CallbackPostConfigure_RunsAfterTheProvidersOwnNormalisation()
    {
        // The provider trims the basket before the callback's PostConfigure runs, so that callback
        // sees an already-normalised value.
        string? seen = null;

        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, Name, b => b
            .Options(ob => ob.Configure(x => x.Basket = "  documents  ")
                .PostConfigure(x => seen = x.Basket)));

        using var provider = services.BuildServiceProvider();

        // Resolving the storage is what materialises the options — IOptions<T> itself is a lazy wrapper.
        _ = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", seen);
    }

    [Fact]
    public void Register_CallbackCanAddValidation()
    {
        var services = new ServiceCollection();

        services.AddFsStorage(_testDir, Name, b => b
            .Options(ob => ob.Configure(x => x.StorageType = "fs")
                .Validate(x => x.StorageType == "xyz", "StorageType must be 'xyz'.")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("'xyz'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_CallbackValidation_AddsToTheBuiltInChecks()
    {
        // The callback's Validate must not replace the provider's own validator: both failures are
        // reported.
        var services = new ServiceCollection();

        services.AddFsStorage(_testDir, Name, b => b
            .Options(ob => ob.Configure(x => x.Basket = "ab")
                .Validate(x => x.StorageType == "xyz", "StorageType must be 'xyz'.")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("Basket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'xyz'", ex.Message, StringComparison.Ordinal);
    }

    // ---------- configuration binding ----------

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemStorage:Basket"] = "documents",
                ["FileSystemStorage:StorageType"] = "fsx",
                ["FileSystemStorage:IsReadOnly"] = "true",
                ["TempFolder:RootPath"] = _testDir,
                ["TempFolder:MaxAge"] = "1.00:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFileSystemFileStorage(Name, b => b
            .FromConfiguration("FileSystemStorage")
            .TempFolder(tb => tb.FromConfiguration("TempFolder")));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal("documents", options.Basket);
        Assert.Equal("fsx", options.StorageType);
        Assert.True(options.IsReadOnly);
        Assert.Equal(Path.GetFullPath(_testDir), TempFolder(provider).RootPath);
    }

    [Fact]
    public void Register_ConfigureCallback_OverridesTheBoundSection()
    {
        // The Options(...) actions are replayed after BindConfiguration, so their Configure has the last word.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemStorage:Basket"] = "from_config",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFileSystemFileStorage(Name, b => b
            .FromConfiguration("FileSystemStorage")
            .Options(ob => ob.Configure(x => x.Basket = "override"))
            .TempFolder(tb => tb.Options(ob => ob.Configure(x => x.RootPath = _testDir))));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("override", Options(provider).Basket);
    }

    [Fact]
    public async Task Register_StorageTtl_SectionMaxAgeWins()
    {
        // The TempFolder section is an explicit part of the storage's configuration: a MaxAge
        // bound from it must survive the registration's own 30-day default.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TempFolder:RootPath"] = _testDir,
                ["TempFolder:TouchDebounce"] = "00:00:00",
                ["TempFolder:MaxAge"] = "01:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFileSystemFileStorage(Name, b => b.TempFolder(tb => tb.FromConfiguration("TempFolder")));

        using var provider = services.BuildServiceProvider();
        var fileId = await UploadAsync(provider, "section-ttl.txt");

        AgeFolders(TimeSpan.FromHours(2));

        await TempFolder(provider).CleanupExpiredAsync(TestContext.Current.CancellationToken);

        Assert.False(await ExistsAsync(provider, fileId));
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration is only called when a path is supplied, so a container with no
        // IConfiguration at all must still resolve and keep the defaults.
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, Name, b => b.Options(ob => ob.Configure(x => x.Basket = "documents")));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", storage.Basket);
        Assert.Equal(FileSystemStorageOptions.DefaultStorageType, storage.StorageType);
    }

    // ---------- what the storage actually receives ----------

    [Fact]
    public void Register_CarriesEveryPropertyThrough()
    {
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, Name, b => b.Options(ob => ob.Configure(x =>
        {
            x.Basket = "documents";
            x.StorageType = "fsx";
            x.IsReadOnly = true;
        })));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", storage.Basket);
        Assert.Equal("fsx", storage.StorageType);
        Assert.True(storage.IsReadOnly);
    }

    [Fact]
    public async Task Register_UsesTheConfiguredRoot()
    {
        // The write reaches a real file under the configured root — proof the value survived
        // configuration rather than reverting to the system temp directory.
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        using var content = new MemoryStream("tiny"u8.ToArray());
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "tiny.txt", TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.AbsoluteUrl));
        Assert.StartsWith(Path.GetFullPath(_testDir), result.AbsoluteUrl, StringComparison.Ordinal);
    }

    // ---------- storage TTL carried by the temp folder ----------

    [Fact]
    public async Task Register_StorageTtl_DefaultsTo30Days_Survives29Days()
    {
        var provider = BuildWithDefaultTtl();
        var fileId = await UploadAsync(provider, "survive.txt");

        AgeFolders(TimeSpan.FromDays(29));

        await TempFolder(provider).CleanupExpiredAsync(TestContext.Current.CancellationToken);

        Assert.True(await ExistsAsync(provider, fileId));
    }

    [Fact]
    public async Task Register_StorageTtl_DefaultsTo30Days_ExpiresAfter31Days()
    {
        var provider = BuildWithDefaultTtl();
        var fileId = await UploadAsync(provider, "expire.txt");

        AgeFolders(TimeSpan.FromDays(31));

        await TempFolder(provider).CleanupExpiredAsync(TestContext.Current.CancellationToken);

        Assert.False(await ExistsAsync(provider, fileId));
    }

    [Fact]
    public async Task Register_StorageTtl_ExplicitMaxAgeWins()
    {
        var services = new ServiceCollection();
        services.AddFsStorageWithTempFolder(Name, tb => tb.Options(ob => ob.Configure(o =>
        {
            o.RootPath = _testDir;
            o.TouchDebounce = TimeSpan.Zero;
            o.MaxAge = TimeSpan.FromHours(1);
        })));

        using var provider = services.BuildServiceProvider();
        var fileId = await UploadAsync(provider, "short.txt");

        AgeFolders(TimeSpan.FromHours(2));

        await TempFolder(provider).CleanupExpiredAsync(TestContext.Current.CancellationToken);

        Assert.False(await ExistsAsync(provider, fileId));
    }

    // ---------- service collection shape ----------

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddFsStorage(_testDir);

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsExactlyOneFileStorage()
    {
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir);

        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    [Fact]
    public void Register_AddsTheValidator()
    {
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<FileSystemStorageOptions>));
    }

    [Fact]
    public void Register_RejectsASecondCallWithTheSameName()
    {
        // One storage per name: two registrations under the same key would fight over the keyed temp
        // folder and the named options instance.
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFsStorage(_testDir));

        Assert.Contains("already registered", ex.Message, StringComparison.Ordinal);
        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    [Fact]
    public void Register_DifferentNames_AreIndependent()
    {
        var secondRoot = Path.Combine(_testDir, "second");

        var services = new ServiceCollection();
        services.AddFsStorage(_testDir, "one", b => b.Options(ob => ob.Configure(x => x.Basket = "basket_a")));
        services.AddFsStorage(secondRoot, "two", b => b.Options(ob => ob.Configure(x => x.Basket = "basket_b")));

        using var provider = services.BuildServiceProvider();

        var storages = provider.GetServices<IFileStorage>().ToList();
        Assert.Equal(2, storages.Count);
        Assert.Contains(storages, s => s.Basket == "basket_a");
        Assert.Contains(storages, s => s.Basket == "basket_b");

        Assert.Equal(Path.GetFullPath(_testDir), provider.GetRequiredKeyedService<ITempFolder>("one").RootPath);
        Assert.Equal(Path.GetFullPath(secondRoot), provider.GetRequiredKeyedService<ITempFolder>("two").RootPath);
    }

    // ---------- TimeProvider ----------

    [Fact]
    public void Register_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Register_RespectsAPreviouslyRegisteredTimeProvider()
    {
        var fake = new TimeProviderStub(DateTimeOffset.UnixEpoch);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task Register_UsesTheRegisteredTimeProviderForUploadedAt()
    {
        var expected = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new TimeProviderStub(expected));
        services.AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        using var content = new MemoryStream("x"u8.ToArray());
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "x.txt", TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.UploadedAt);
    }

    // ---------- helpers ----------

    private ServiceProvider BuildWithDefaultTtl()
    {
        var services = new ServiceCollection();
        services.AddFsStorageWithTempFolder(Name, tb => tb.Options(ob => ob.Configure(o =>
        {
            o.RootPath = _testDir;
            // Zero debounce: the activity marker lands synchronously with the write, so the test
            // can age the folders deterministically instead of racing a pending touch.
            o.TouchDebounce = TimeSpan.Zero;
        })));

        return services.BuildServiceProvider();
    }

    private static async Task<string> UploadAsync(IServiceProvider provider, string fileName)
    {
        var storage = provider.GetRequiredService<IFileStorage>();
        using var content = new MemoryStream("payload"u8.ToArray());

        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = fileName, TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        return result.FileId;
    }

    private static async Task<bool> ExistsAsync(IServiceProvider provider, string fileId)
    {
        var storage = provider.GetRequiredService<IFileStorage>();

        return await storage.DownloadAsync(
            fileId,
            static (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Backdates the folder chain the write touched to <paramref name="age"/> ago, so the age-based
    /// cleanup sees the storage subtree as older than it really is.
    /// </summary>
    private void AgeFolders(TimeSpan age)
    {
        var stamp = DateTime.UtcNow - age;
        var basketDir = Path.Combine(_testDir, FileSystemStorageOptions.DefaultBasket);
        var tenantDir = Path.Combine(basketDir, "1");

        Directory.SetLastWriteTimeUtc(basketDir, stamp);
        Directory.SetLastWriteTimeUtc(tenantDir, stamp);
    }

    private sealed class TimeProviderStub(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
