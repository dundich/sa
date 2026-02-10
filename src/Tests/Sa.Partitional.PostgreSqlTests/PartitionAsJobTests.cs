using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSqlTests;


public class PartitionAsJobTests(PartitionAsJobTests.Fixture fixture) : IClassFixture<PartitionAsJobTests.Fixture>
{

    public class Fixture : PgDataSourceFixture<IPartRepository>
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
                    .AddMigration("RU", ["Moscow", "Samara"])
                    .AddMigration("USA", ["Alabama", "New York"])
                    .AddMigration("FR", ["Paris", "Lyon", "Bordeaux"]);

                });
            }
            , AsBackgroundJob: true
            )
            .AddDataSource(configure => configure.WithConnectionString(_ => this.ConnectionString))
            ;
        }
    }


    private IPartRepository Sub => fixture.Sub;


    public static DateTimeOffset StartOfDay(DateTimeOffset dateTime) => new(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, 0, dateTime.Offset);


    [Fact]
    public async Task MigrateAsJobTest()
    {
        Console.WriteLine(fixture.ConnectionString);

        int i = fixture.ServiceProvider.GetRequiredService<IScheduler>().Start(CancellationToken.None);
        Assert.True(i > 0);

        await Task.Delay(800, TestContext.Current.CancellationToken);

        var list = await Sub.GetPartsFromDate("customer", StartOfDay(DateTimeOffset.Now), TestContext.Current.CancellationToken);
        Assert.NotEmpty(list);
    }
}
