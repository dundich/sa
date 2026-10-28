using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;
using System.Text;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// The two write-side guards: <see cref="PostgresFileStorageOptions.IsReadOnly"/> and
/// <see cref="PostgresFileStorageOptions.MaxUploadSizeBytes"/>. Both are configured per
/// registration, so one container hosts two storages over two tables.
/// </summary>
public sealed class PostgresFileStorageGuardsTests(PostgresFileStorageGuardsTests.Fixture fixture)
    : IClassFixture<PostgresFileStorageGuardsTests.Fixture>
{
    private const string ReadOnlyKey = "guarded_readonly";
    private const string LimitedKey = "guarded_limited";
    private const int MaxBytes = 32;

    public sealed class Fixture : PgDataSourceFixture<IFileStorage>
    {
        public Fixture()
        {
            Register(ReadOnlyKey, o => o.IsReadOnly = true);
            Register(LimitedKey, o => o.MaxUploadSizeBytes = MaxBytes);
        }

        public IFileStorage ReadOnly => ServiceProvider.GetRequiredKeyedService<IFileStorage>(ReadOnlyKey);

        public IFileStorage Limited => ServiceProvider.GetRequiredKeyedService<IFileStorage>(LimitedKey);

        private void Register(string key, Action<PostgresFileStorageOptions> configure)
            => Services.AddSaPostgreSqlFileStorageChained(key, b => b.Options(ob => ob.Configure(o =>
                {
                    o.TableName = key;
                    configure(o);
                })))
                .AddDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)));
    }

    // ---------- IsReadOnly ----------

    [Fact]
    public void ReadOnly_ReportsItself()
    {
        Assert.True(fixture.ReadOnly.IsReadOnly);
        Assert.Equal("pg", fixture.ReadOnly.StorageType);
        Assert.Equal(StorageNaming.DefaultBasket, fixture.ReadOnly.Basket);
    }

    [Fact]
    public async Task ReadOnly_Upload_Throws()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("x"));

        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            fixture.ReadOnly.UploadAsync(
                new UploadFileInput { FileName = "a.txt", TenantId = 1 },
                content, fixture.CancellationToken));
    }

    [Fact]
    public async Task ReadOnly_Delete_Throws()
    {
        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            fixture.ReadOnly.DeleteAsync(
                "pg://share/1/1773210911/a.txt", fixture.CancellationToken));
    }

    [Fact]
    public async Task ReadOnly_UploadWithNullStream_ReportsTheBadArgument()
    {
        // The null check runs before the read-only guard: the caller learns about the argument it
        // actually got wrong, not about the storage's mode.
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.ReadOnly.UploadAsync(
                new UploadFileInput { FileName = "a.txt", TenantId = 1 },
                null!, fixture.CancellationToken));
    }

    [Fact]
    public async Task NullFileIdOrCallback_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.Limited.DeleteAsync(null!, fixture.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.Limited.DownloadAsync(
                null!, (_, _) => Task.CompletedTask, fixture.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.Limited.DownloadAsync(
                "pg://share/1/1773210911/a.txt", null!, fixture.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.Limited.GetMetadataAsync(null!, fixture.CancellationToken));
    }

    // ---------- MaxUploadSizeBytes ----------

    [Fact]
    public async Task Limit_AcceptsAnUploadOfExactlyTheLimit()
    {
        var upload = await Upload(fixture.Limited, "exact.bin", new byte[MaxBytes], seekable: true);

        long size = await fixture.DataSource.ExecuteScalar<long>(
            $"SELECT size FROM public.{LimitedKey} WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", upload.FileId)),
            fixture.CancellationToken);

        Assert.Equal((long)MaxBytes, size);
    }

    [Theory]
    [InlineData(true)]   // seekable: rejected before any partition DDL
    [InlineData(false)]  // non-seekable: measured while it is buffered
    public async Task Limit_RejectsAnOversizedUpload_AndLeavesNoRow(bool seekable)
    {
        // A first, allowed upload makes sure the table and the partition exist, so "no row" below
        // is about the rejected upload rather than a missing relation.
        await Upload(fixture.Limited, "small.bin", new byte[8], seekable: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Upload(fixture.Limited, "oversized.bin", new byte[MaxBytes + 1], seekable));

        Assert.Contains(
            nameof(PostgresFileStorageOptions.MaxUploadSizeBytes), ex.Message, StringComparison.Ordinal);

        long count = await fixture.DataSource.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM public.{LimitedKey} WHERE name = @name",
            cmd => cmd.Parameters.Add(new("name", "oversized.bin")),
            fixture.CancellationToken);

        Assert.Equal(0L, count);
    }

    private static async Task<StorageResult> Upload(
        IFileStorage storage,
        string fileName,
        byte[] payload,
        bool seekable)
    {
        var input = new UploadFileInput { FileName = fileName, TenantId = 3 };

        if (seekable)
        {
            using var content = new MemoryStream(payload);
            return await storage.UploadAsync(input, content, TestContext.Current.CancellationToken);
        }

        await using var inner = new MemoryStream(payload);
        await using var nonSeekable = new NonSeekableStream(inner);
        return await storage.UploadAsync(input, nonSeekable, TestContext.Current.CancellationToken);
    }
}
