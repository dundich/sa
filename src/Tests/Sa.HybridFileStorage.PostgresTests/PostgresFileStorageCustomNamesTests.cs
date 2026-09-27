using System.Text;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// A provider registered under non-default names must actually work end to end. It did not:
/// the DDL registered the table under the raw <c>TableName</c> while the provider queried a
/// <c>Sanitize</c>-rewritten one, and the provider's <c>StorageType</c> property disagreed with
/// the scheme prefix it matched file IDs against.
/// </summary>
public sealed class PostgresFileStorageCustomNamesTests(PostgresFileStorageCustomNamesTests.Fixture fixture)
    : IClassFixture<PostgresFileStorageCustomNamesTests.Fixture>
{
    private const string DataContent = "custom names";

    public sealed class Fixture : PostgresFileStorageFixture
    {
        public Fixture() : base("custom_files", opts =>
        {
            opts.StorageType = "pgc";
            opts.Basket = "archive";
        })
        { }
    }

    private IFileStorage Sub => fixture.Sub;

    [Fact]
    public void Storage_ExposesTheConfiguredNames()
    {
        Assert.Equal("pgc", Sub.StorageType);
        Assert.Equal("archive", Sub.Basket);
        Assert.False(Sub.IsReadOnly);
    }

    [Fact]
    public async Task RoundTrip_UsesTheConfiguredTableAndProducesParsableFileIds()
    {
        var input = new UploadFileInput { FileName = "custom.txt", TenantId = 5 };
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(DataContent));

        var upload = await Sub.UploadAsync(input, content, fixture.CancellationToken);

        // The scheme must be exactly what StorageType reports — otherwise CanProcess, which
        // matches on the same value, would reject the storage's own file ID.
        Assert.StartsWith("pgc://archive/5/", upload.FileId);
        Assert.Equal("pgc", upload.StorageType);
        Assert.True(Sub.CanProcess(upload.FileId));

        // The row must be in the table that was registered, under the name that was configured.
        long count = await fixture.DataSource.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM public.custom_files WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", upload.FileId)), fixture.CancellationToken);
        Assert.Equal(1, count);

        string? actual = null;
        bool found = await Sub.DownloadAsync(upload.FileId,
            async (s, _) => actual = await new StreamReader(s, Encoding.UTF8).ReadToEndAsync(),
            fixture.CancellationToken);

        Assert.True(found);
        Assert.Equal(DataContent, actual);

        Assert.True(await Sub.DeleteAsync(upload.FileId, fixture.CancellationToken));
    }

    [Fact]
    public async Task SizeColumn_IsBigInt_AndRecordsTheRealLength()
    {
        // An INT column silently truncated the recorded size of anything over 2 GB, because the
        // provider also cast the stream length to int before sending it. The root table only
        // exists once a partition has been created, so upload first.
        byte[] payload = new byte[4096];
        Array.Fill(payload, (byte)7);

        var input = new UploadFileInput { FileName = "sized.bin", TenantId = 9 };
        using var content = new MemoryStream(payload);

        var upload = await Sub.UploadAsync(input, content, fixture.CancellationToken);

        long size = await fixture.DataSource.ExecuteScalar<long>(
            "SELECT size FROM public.custom_files WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", upload.FileId)), fixture.CancellationToken);

        Assert.Equal(payload.Length, size);

        string? dataType = await fixture.DataSource.ExecuteScalar<string>(
            """
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'custom_files' AND column_name = 'size'
            """,
            cmd => { }, fixture.CancellationToken);

        Assert.Equal("bigint", dataType, ignoreCase: true);
    }
}
