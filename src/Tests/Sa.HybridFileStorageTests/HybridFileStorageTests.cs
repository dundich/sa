using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorageTests;

public class HybridFileStorageTests(HybridFileStorageTests.Fixture fixture)
    : IClassFixture<HybridFileStorageTests.Fixture>
{
    public sealed class Fixture : SaFixture<IHybridFileStorage, FileSystemStorageOptions>
    {
        public Fixture()
            : base(new FileSystemStorageOptions
            {
                BasePath = "hybrid_test"
            })
        {
            SetupServices = (services, cfg)
                => services
                    .AddFileSystemFileStorage(Settings)
                    .AddInMemoryFileStorage()
                    .AddHybridFileStorage(b
                        => b.ConfigureInterceptors((_, c)
                            => c.AddUploadInterceptor(new MemUploadSomeInterceptor())));
        }

        public override ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Settings.BasePath, true);
            }
            catch
            {
                // suppress error
            }
            return base.DisposeAsync();
        }
    }

    class MemUploadSomeInterceptor : IUploadInterceptor
    {
        public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(input.FileName != "some.bin" || storage.StorageType == InMemoryFileStorage.DefaultStorageType);
        }

        public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private IHybridFileStorage Storage => fixture.Sub;


    [Fact]
    public async Task Crud()
    {
        var input = new UploadFileInput { FileName = "test.bin", TenantId = 2 };
        using MemoryStream fileContent = FixtureHelper.GetByteStream();

        var result = await Storage.UploadAsync(input, fileContent, fixture.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);

        bool canProcessed = Storage.CanProcess(result.FileId);
        Assert.True(canProcessed);

        var isSame = await EnsureFileSame(result.FileId, fileContent);
        Assert.True(isSame);

        var isDeleted = await Storage.DeleteAsync(result.FileId, fixture.CancellationToken);

        Assert.True(isDeleted);

        isSame = await EnsureFileSame(result.FileId, fileContent);

        Assert.False(isSame);
    }

    [Fact]
    public async Task UploadInMemStorageBySomeInterceptor()
    {
        var input = new UploadFileInput { FileName = "some.bin", TenantId = 1 };
        using MemoryStream fileContent = FixtureHelper.GetByteStream();

        var result = await Storage.UploadAsync(input, fileContent, fixture.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);

        var isSame = await EnsureFileSame(result.FileId, fileContent);
        Assert.True(isSame);

        var isDeleted = await Storage.DeleteAsync(result.FileId, fixture.CancellationToken);
        Assert.True(isDeleted);
    }

    [Fact]
    public async Task WhenStorageIsEmptyThrowsInvalidOperationException()
    {
        ServiceCollection services = new();
        services.AddHybridFileStorage();

        using var sp = services.BuildServiceProvider();

        using MemoryStream fileContent = FixtureHelper.GetByteStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
           sp.GetRequiredService<IHybridFileStorage>().UploadAsync(new UploadFileInput { FileName = "", TenantId = 1 }, fileContent, fixture.CancellationToken));
    }


    [Fact]
    public async Task WhenStorageIsReadOnlyThrowsInvalidOperationException()
    {
        ServiceCollection services = new();
        services.AddHybridFileStorage(b
            => b.ConfigureStorage((_, c)
                => c.AddStorage(new InMemoryFileStorage(null, true))));

        using var sp = services.BuildServiceProvider();

        using MemoryStream fileContent = FixtureHelper.GetByteStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
           sp.GetRequiredService<IHybridFileStorage>().UploadAsync(new UploadFileInput { FileName = "", TenantId = 1 }, fileContent, fixture.CancellationToken));
    }


    private async Task<bool> EnsureFileSame(string fileName, MemoryStream expectedBytes)
    {
        expectedBytes.Seek(0, SeekOrigin.Begin);

        using MemoryStream memoryStream = new();

        var isDownloaded = await Storage.DownloadAsync(fileName
            , (stream, ct) => stream.CopyToAsync(memoryStream, ct)
            , fixture.CancellationToken);

        if (isDownloaded)
        {
            Assert.Equal(expectedBytes.ToArray(), memoryStream.ToArray());
        }

        return isDownloaded;
    }
}
