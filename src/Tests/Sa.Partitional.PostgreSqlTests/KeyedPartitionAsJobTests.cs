using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// End-to-end check that a keyed module's background jobs actually run: the module uses the shared
/// default data source (it configures none of its own), starts its own named schedule, and the
/// migration job creates the list partitions.
/// </summary>
public class KeyedPartitionAsJobTests(KeyedPartitionAsJobTests.Fixture fixture)
    : IClassFixture<KeyedPartitionAsJobTests.Fixture>
{
    const string ModuleKey = "kp";

    public sealed class Fixture : PgDataSourceFixture<IPgDataSource>
    {
        public Fixture()
        {
            // A single shared data source: the keyed module below configures none of its own and
            // therefore resolves this default one.
            Services.AddSaPostgreSqlDataSource(
                ds => ds.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)));

            Services.AddSaPartitional(ModuleKey, (_, builder) =>
            {
                builder.AddSchema(schema => schema.AddTable("keyed_customer",
                        "id bigint NOT NULL",
                        "country TEXT NOT NULL",
                        "city TEXT NOT NULL")
                    .WithPartSeparator("_")
                    .PartByList("country", "city")
                    .AddMigration("RU", ["Moscow", "Samara"])
                    .AddMigration("USA", ["Alabama", "New York"]));
            }, asBackgroundJob: true);
        }
    }

    static DateTimeOffset StartOfDay(DateTimeOffset dateTime)
        => new(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, 0, dateTime.Offset);

    [Fact]
    public async Task KeyedModule_UsesDefaultDataSource_AndItsMigrationJobRuns()
    {
        IScheduler scheduler = fixture.ServiceProvider.GetRequiredKeyedService<IScheduler>(ModuleKey);

        int started = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(started > 0);

        await Task.Delay(800, TestContext.Current.CancellationToken);

        IPartRepository repository = fixture.ServiceProvider.GetRequiredKeyedService<IPartRepository>(ModuleKey);

        var parts = await repository.GetPartsFromDate(
            "keyed_customer",
            StartOfDay(DateTimeOffset.Now),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(parts);
    }
}
