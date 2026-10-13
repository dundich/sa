using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Cleaning;

public class PartCleanupArchiveTests(PartCleanupArchiveTests.Fixture fixture) : IClassFixture<PartCleanupArchiveTests.Fixture>
{
    public class Fixture : PgDataSourceFixture<IPartCleanupService>
    {
        public Fixture()
            {
                Services.AddSaPartitional((_, builder) =>
                {
                    builder.AddSchema(schema =>
                    {
                        schema.AddTable("cleanup_archive",
                            "id INT NOT NULL",
                            "city TEXT NOT NULL",
                            "tenant_id INT NOT NULL",
                            "payload_id TEXT NOT NULL"
                        )
                        .PartByList("tenant_id", "city")
                        ;
                    });
                })
                .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
                .AddPartCleanupSchedule(b => b.Configure((_, s) =>
                {
                    s.CleanupMode = PartCleanupMode.Archive;
                    s.ArchiveSchemaName = "archive";
                    s.DropPartsAfterRetention = TimeSpan.Zero;
                }))
                ;
    }

    private IPartCleanupService Sub => fixture.Sub;

    private Task<List<PartByRangeInfo>> GetParts()
        => GetPartRep().GetPartsToDate("cleanup_archive", DateTimeOffset.UtcNow.AddDays(200));

    private IPartRepository GetPartRep() => fixture.ServiceProvider.GetRequiredService<IPartRepository>();

        await GetPartRep().Migrate([DateTimeOffset.UtcNow.AddDays(-2)], table =>
        {
            StrOrNum[][] result = table switch
            {
                "public.cleanup_archive" => [[1, "New York"], [2, "Moscow"]],
                _ => []
            };
            return Task.FromResult(result);
        }, CancellationToken.None);

        await GetPartRep().Migrate([DateTimeOffset.UtcNow.AddDays(-2)], CancellationToken.None);

        // ensure cache has parts
        await GetPartRep().GetPartsToDate("cleanup_archive", DateTimeOffset.UtcNow.AddDays(10), CancellationToken.None);
    }

    [Fact]
    public async Task ArchiveMode_MovesPartsToArchiveSchema_AndMarksArchived()
    {
        await MigrateTest();

        List<PartByRangeInfo> partsBefore = await GetPartRep().GetPartsFromDate("cleanup_archive", DateTimeOffset.UtcNow.AddDays(-100));
        Assert.NotEmpty(partsBefore);
        int totalBefore = partsBefore.Count;

        DateTimeOffset toDate = DateTimeOffset.UtcNow.AddDays(100);

        int archived = await Sub.Clean(toDate, CancellationToken.None);
        Assert.Equal(totalBefore, archived);

        List<PartByRangeInfo> partsAfter = await GetPartsFromDateNow();
        Assert.Empty(partsAfter);

        string suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        string sql = $"SELECT to_regclass('archive.\"cleanup_archive__1__New York__y{suffix[0..4]}m{suffix[4..6]}d{suffix[6..8]}\"') IS NOT NULL";
        object? exists = await fixture.DataSource.ExecuteScalar(sql, CancellationToken.None);
        Assert.True(exists is bool b && b);
    }

    private Task<List<PartByRangeInfo>> GetPartsFromDateNow()
        => GetPartRep().GetPartsFromDate("cleanup_archive", DateTimeOffset.UtcNow);
}