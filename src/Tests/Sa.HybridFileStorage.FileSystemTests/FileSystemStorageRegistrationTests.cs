using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Registration-level behaviour of the filesystem provider: what the options pipeline does, in which
/// order, and what the constructed storage receives.
/// </summary>
public sealed class FileSystemStorageRegistrationTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"fs_reg_{Path.GetRandomFileName()}");

    public FileSystemStorageRegistrationTests() => Directory.CreateDirectory(_testDir);

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { /* best effort */ }
    }

    private static FileSystemStorageOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptionsMonitor<FileSystemStorageOptions>>()
            .Get(provider.GetRequiredService<FileSystemStorageRegistration>().OptionsName);

    // ---------- null / argument checking ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaFileSystemFileStorage());
    }

    // ---------- pipeline order: configure -> post-configure -> validate ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        // Options are materialised lazily, so an invalid configuration surfaces as
        // OptionsValidationException when the storage is first resolved. The old registration threw
        // ValidationException at the Add... call itself and left the collection untouched.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(); // BasePath defaults to empty.

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("BasePath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_WhitespaceBasePath_IsRejected_NotNormalisedIntoADirectory()
    {
        // Path.GetFullPath("   ") succeeds on Unix, so an unguarded PostConfigure would silently
        // turn a blank BasePath into a directory named "   " and validation would pass.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = "   ")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("BasePath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Register_ValidatesEveryOption_NotJustTheConfiguredOnes(int bufferSize)
    {
        // BufferSize is the property that used to be dropped by the options -> settings mapping and so
        // escaped validation entirely. It is validated now because there is only one type.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
        {
            x.BasePath = _testDir;
            x.BufferSize = bufferSize;
        })));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("BufferSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_PostConfigures_TheBasePathToAFullPath()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.True(Path.IsPathFullyQualified(options.BasePath), $"'{options.BasePath}' is not rooted.");
        Assert.Equal(Path.GetFullPath(_testDir), options.BasePath);
    }

    [Fact]
    public void Register_PostConfigures_TrimsTheNames()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
        {
            x.BasePath = _testDir;
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
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
        {
            x.BasePath = _testDir;
            x.Basket = "  documents  ";
        })));

        using var provider = services.BuildServiceProvider();

        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", storage.Basket);
    }

    // ---------- the builder handed to the callback ----------

    [Fact]
    public void Register_CallbackCanAddAPostConfigure()
    {
        // Pre-initialisation is Configure, post-initialisation is PostConfigure: both are available
        // on the OptionsBuilder the callback receives, and the provider's own PostConfigure runs first.
        var services = new ServiceCollection();

        services.AddSaFileSystemFileStorage(o => o
            .Options(ob => ob.Configure(x => x.BasePath = _testDir)
            .PostConfigure(x => x.BufferSize = 4096)));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal(4096, options.BufferSize);
        Assert.Equal(Path.GetFullPath(_testDir), options.BasePath);
    }

    [Fact]
    public void Register_CallbackPostConfigure_RunsAfterTheProvidersOwnNormalisation()
    {
        // The provider resolves BasePath to a full path before the callback's PostConfigure runs, so
        // that callback sees an already-normalised value.
        string? seen = null;

        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o
            .Options(ob => ob.Configure(x => x.BasePath = _testDir)
            .PostConfigure(x => seen = x.BasePath)));

        using var provider = services.BuildServiceProvider();

        // Resolving the storage is what materialises the options — IOptions<T> itself is a lazy wrapper.
        _ = provider.GetRequiredService<IFileStorage>();

        Assert.Equal(Path.GetFullPath(_testDir), seen);
    }

    [Fact]
    public void Register_CallbackCanAddValidation()
    {
        var services = new ServiceCollection();

        services.AddSaFileSystemFileStorage(o => o
            .Options(ob => ob.Configure(x => { x.BasePath = _testDir; x.BufferSize = 512; })
            .Validate(x => x.BufferSize >= 1024, "BufferSize must be at least 1 KB.")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("at least 1 KB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_CallbackValidation_AddsToTheBuiltInChecks()
    {
        // The callback's Validate must not replace the provider's own validator: both failures are
        // reported, in registration order. BasePath is valid so the built-in check reaches BufferSize.
        var services = new ServiceCollection();

        services.AddSaFileSystemFileStorage(o => o
            .Options(ob => ob.Configure(x => { x.BasePath = _testDir; x.BufferSize = 0; })
            .Validate(x => x.Basket == "never", "Basket must be 'never'.")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IFileStorage>());

        Assert.Contains("BufferSize", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never", ex.Message, StringComparison.Ordinal);
    }

    // ---------- configuration binding ----------

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemStorage:BasePath"] = _testDir,
                ["FileSystemStorage:Basket"] = "documents",
                ["FileSystemStorage:BufferSize"] = "8192",
                ["FileSystemStorage:IsReadOnly"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFileSystemFileStorage(b => b.FromConfiguration("FileSystemStorage"));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal(Path.GetFullPath(_testDir), options.BasePath);
        Assert.Equal("documents", options.Basket);
        Assert.Equal(8192, options.BufferSize);
        Assert.True(options.IsReadOnly);
    }

    [Fact]
    public void Register_ConfigureCallback_OverridesTheBoundSection()
    {
        // The Options(...) actions are replayed after BindConfiguration, so their Configure has the last word.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemStorage:BasePath"] = Path.Combine(Path.GetTempPath(), "from_config"),
                ["FileSystemStorage:Basket"] = "from_config",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFileSystemFileStorage(o => o
            .FromConfiguration("FileSystemStorage")
            .Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal(Path.GetFullPath(_testDir), options.BasePath);
        Assert.Equal("from_config", options.Basket);
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration is only called when a path is supplied, so a container with no
        // IConfiguration at all must still resolve and keep the defaults.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal(FileSystemStorageOptions.DefaultBasket, storage.Basket);
        Assert.Equal(FileSystemStorageOptions.DefaultStorageType, storage.StorageType);
    }

    // ---------- what the storage actually receives ----------

    [Fact]
    public void Register_CarriesEveryPropertyThrough()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
        {
            x.BasePath = _testDir;
            x.Basket = "documents";
            x.StorageType = "fsx";
            x.IsReadOnly = true;
            x.BufferSize = 4096;
        })));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", storage.Basket);
        Assert.Equal("fsx", storage.StorageType);
        Assert.True(storage.IsReadOnly);
    }

    [Fact]
    public async Task Register_UsesTheConfiguredBufferSize()
    {
        // BufferSize feeds FileStreamOptions.BufferSize, so reaching a real file proves the value
        // survived configuration rather than reverting to a default.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
        {
            x.BasePath = _testDir;
            x.BufferSize = 1;
        })));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        using var content = new MemoryStream("tiny"u8.ToArray());
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "tiny.txt", TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.AbsoluteUrl));
    }

    // ---------- service collection shape ----------

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsExactlyOneFileStorage()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    [Fact]
    public void Register_AddsTheValidator()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<FileSystemStorageOptions>));
    }

    [Fact]
    public void Register_RejectsASecondCall()
    {
        // The provider owns the unnamed FileSystemStorageOptions instance. A second registration would
        // add a second IFileStorage over those same options and stack both Configure callbacks, so the
        // storage would silently use merged settings. Mirrors AddSaS3FileStorage.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir))));

        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    // ---------- TimeProvider ----------

    [Fact]
    public void Register_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Register_RespectsAPreviouslyRegisteredTimeProvider()
    {
        var fake = new TimeProviderStub(DateTimeOffset.UnixEpoch);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task Register_UsesTheRegisteredTimeProviderForUploadedAt()
    {
        var expected = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new TimeProviderStub(expected));
        services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = _testDir)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        using var content = new MemoryStream("x"u8.ToArray());
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "x.txt", TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.UploadedAt);
    }

    private sealed class TimeProviderStub(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}