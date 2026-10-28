using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// Reads against a database where nothing has been written yet. The table is created by the first
/// upload (<c>EnsureParts</c>) or by the migration schedule, so a read that runs before either must
/// answer "not found" — a missing table is not an error, and in the hybrid chain an exception would
/// be treated as failover fuel (and surface as an aggregate exception when no storage answers).
/// </summary>
public sealed class PostgresFileStorageEmptyTableTests(
    PostgresFileStorageEmptyTableTests.Fixture fixture)
    : IClassFixture<PostgresFileStorageEmptyTableTests.Fixture>
{
    private const string NoTableKey = "empty_files";
    private const string NoSchemaKey = "empty_schema_files";

    public sealed class Fixture : PgDataSourceFixture<IFileStorage>
    {
        public Fixture()
        {
            Register(NoTableKey, schema: null);
            Register(NoSchemaKey, schema: "absent_schema");
        }

        public IFileStorage NoTable => ServiceProvider.GetRequiredKeyedService<IFileStorage>(NoTableKey);

        public IFileStorage NoSchema => ServiceProvider.GetRequiredKeyedService<IFileStorage>(NoSchemaKey);

        private void Register(string key, string? schema)
            => Services.AddSaPostgreSqlFileStorageChained(key, b => b.Options(ob => ob.Configure(o =>
                {
                    o.TableName = key;
                    o.SchemaName = schema;
                })))
                .AddDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)));
    }

    [Fact]
    public Task ReadsOnAMissingTable_AnswerNotFound() => AssertNotFound(fixture.NoTable);

    [Fact]
    public Task ReadsOnAMissingSchema_AnswerNotFound() => AssertNotFound(fixture.NoSchema);

    private static async Task AssertNotFound(IFileStorage storage)
    {
        const string fileId = "pg://share/1/1773210911/never-uploaded.bin";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await storage.GetMetadataAsync(fileId, cancellationToken));

        bool called = false;

        bool found = await storage.DownloadAsync(
            fileId,
            (_, _) => { called = true; return Task.CompletedTask; },
            cancellationToken);

        Assert.False(found);
        Assert.False(called);

        Assert.False(await storage.DeleteAsync(fileId, cancellationToken));
    }
}
