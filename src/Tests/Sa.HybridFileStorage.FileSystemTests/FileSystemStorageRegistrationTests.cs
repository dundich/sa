using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Registration-level behaviour of the filesystem provider: what is validated, when, and what
/// survives into the constructed storage.
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

    // ---------- null / validation ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir }));
    }

    [Fact]
    public void Register_Rejects_NullSettings()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddSaFileSystemFileStorage((FileSystemStorageSettings)null!));
    }

    [Fact]
    public void Register_Rejects_NullConfigure()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddSaFileSystemFileStorage((Action<FileSystemStorageOptions>)null!));
    }

    [Fact]
    public void Register_ValidatesEagerly_AndLeavesTheCollectionUntouched()
    {
        // Previously the mapping to FileSystemStorageSettings happened lazily inside the
        // singleton factory, so validation of the mapped object could not fail fast.
        var services = new ServiceCollection();

        Assert.Throws<ValidationException>(() =>
            services.AddSaFileSystemFileStorage(o => o.BasePath = "   "));
        Assert.Empty(services);
    }

    [Fact]
    public void Register_ValidatesTheMappedSettings_NotJustTheOptions()
    {
        // A property that exists on the options but is dropped by the mapping would escape
        // validation. BufferSize is exactly such a property: it used to be left at its default.
        var services = new ServiceCollection();

        var ex = Assert.Throws<ValidationException>(() =>
            services.AddSaFileSystemFileStorage(o =>
            {
                o.BasePath = _testDir;
                o.BufferSize = 0;
            }));
        Assert.Contains("BufferSize", ex.Message, StringComparison.Ordinal);
    }

    // ---------- what the storage actually receives ----------

    [Fact]
    public void Register_WithOptions_CarriesEveryPropertyThrough()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o =>
        {
            o.BasePath = _testDir;
            o.Basket = "documents";
            o.StorageType = "fsx";
            o.IsReadOnly = true;
            o.BufferSize = 4096;
        });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("documents", storage.Basket);
        Assert.Equal("fsx", storage.StorageType);
        Assert.True(storage.IsReadOnly);
    }

    [Fact]
    public async Task Register_WithOptions_UsesTheConfiguredBufferSize()
    {
        // BufferSize feeds FileStreamOptions.BufferSize. Reaching this far requires the value to
        // have survived the options -> settings mapping, which is what used to drop it.
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o =>
        {
            o.BasePath = _testDir;
            o.BufferSize = 1;
        });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        using var content = new MemoryStream("tiny"u8.ToArray());
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "tiny.txt", TenantId = 1 },
            content,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.AbsoluteUrl));
    }

    [Fact]
    public void Register_AddsExactlyOneFileStorage()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => o.BasePath = _testDir);

        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    [Fact]
    public void Register_IsRepeatable_SoSeveralStoragesCanCoexist()
    {
        // Unlike S3 and Postgres, each filesystem storage owns its own directory, so a second
        // registration is a legitimate call and must not be rejected.
        string other = Path.Combine(_testDir, "second");

        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(o => { o.BasePath = _testDir; o.StorageType = "a"; });
        services.AddSaFileSystemFileStorage(o => { o.BasePath = other; o.StorageType = "b"; });

        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IFileStorage)));

        using var provider = services.BuildServiceProvider();
        var storages = provider.GetServices<IFileStorage>().ToList();

        Assert.Equal(2, storages.Count);
        Assert.Contains(storages, s => s.StorageType == "a");
        Assert.Contains(storages, s => s.StorageType == "b");
    }

    [Fact]
    public void Register_SnapshotsTheSettings_SoLaterMutationCannotChangeTheStorage()
    {
        var settings = new FileSystemStorageSettings { BasePath = _testDir, StorageType = "before" };

        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(settings);

        settings = settings with { StorageType = "after" };

        using var provider = services.BuildServiceProvider();

        Assert.Equal("before", provider.GetRequiredService<IFileStorage>().StorageType);
    }

    // ---------- TimeProvider ----------

    [Fact]
    public void Register_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Register_RespectsAPreviouslyRegisteredTimeProvider()
    {
        var fake = new TimeProviderStub(DateTimeOffset.UnixEpoch);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task Register_UsesTheRegisteredTimeProviderForUploadedAt()
    {
        var expected = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new TimeProviderStub(expected));
        services.AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

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
