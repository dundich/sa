using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSqlTests;


public class PartitionAsJobTests(PartitionAsJobTests.Fixture fixture)
    : IClassFixture<PartitionAsJobTests.Fixture>
{

    public sealed class Fixture : PgDataSourceFixture<IPartRepository>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    // Настройка таблицы customers
                    schema.AddTable("customer",
                        "id INT NOT NULL",
                        "country TEXT NOT NULL",
                        "city TEXT NOT NULL"
                    )
                    // разделить в таблицах меж партиций
                    .WithPartSeparator("_")
                    // Партиционирование по country и city (если не задан PartByRange то по дням)
                    .PartByList("country", "city")
                    // Миграция партиций каждого тенанта по city
                    .AddMigrationChildren("RU", "Moscow", "Samara")
                    .AddMigrationChildren("USA", "Alabama", "New York")
                    .AddMigrationChildren("FR", "Paris", "Lyon", "Bordeaux");

                });
            }
            , asBackgroundJob: true
            )
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            ;
        }
    }


    private IPartRepository Sub => fixture.Sub;


    public static DateTimeOffset StartOfDay(DateTimeOffset dateTime) => new(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, 0, dateTime.Offset);


    [Fact]
    public async Task MigrateAsJobTest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        int i = await fixture.ServiceProvider
            .GetRequiredService<IScheduler>().StartAsync(ct);
        Assert.True(i > 0);

        // The job runs on the scheduler's thread: wait for the partitions it creates instead of
        // sleeping for a hard-coded 800 ms (which flakes on a loaded machine).
        await AwaitHelper.UntilTrueAsync(
            async () => (await Sub.GetPartsFromDate("customer", StartOfDay(DateTimeOffset.Now), ct)).Count > 0,
            TimeSpan.FromSeconds(30),
            ct);
    }
}
