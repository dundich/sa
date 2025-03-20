using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Partitional.ConsoleApp;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Partitional.PostgreSql;


Console.WriteLine("Hello, Partitional.PostgreSql!");

var connectionString = "Host=localhost;Username=;Password=;Database=test_1";


var hostBuilder = Host.CreateApplicationBuilder(args);

hostBuilder.Services
    .AddSingleton<Tester>()
    .AddLogging(c => c.AddConsole())
    .AddPgDataSource(builder => builder.WithConnectionString(connectionString))
    .AddPartitional((sp, builder) =>
    {
        builder.AddSchema("public", schema =>
        {
            // Configure the 'customer' table
            schema.AddTable("customer",
                "id INT NOT NULL",
                "country TEXT NOT NULL",
                "city TEXT NOT NULL"
            )
            // Separate partitions in tables
            .WithPartSeparator("_")
            // Partition by 'country' and 'city' (if PartByRange is not specified, defaults to daily)
            .PartByList("country", "city")
            // Migration of partitions for each tenant by city
            .AddMigration("RU", ["Moscow", "Samara"])
            .AddMigration("USA", ["Alabama", "New York"])
            .AddMigration("FR", ["Paris", "Lyon", "Bordeaux"]);
        });


    })
    // Schedule for creating new partitions
    .AddPartMigrationSchedule((sp, opts) =>
    {
        opts.AsJob = true;
        opts.ExecutionInterval = TimeSpan.FromHours(2);
        opts.ForwardDays = 2;
    })
    // Schedule for removing old partitions
    .AddPartCleanupSchedule((sp, opts) =>
    {
        opts.AsJob = true;
        opts.DropPartsAfterRetention = TimeSpan.FromDays(21);
    });


using IHost host = hostBuilder.Build();


await host.Services.GetRequiredService<Tester>()
    .ShouldRunTest(host.Services.GetRequiredService<IPartitionManager>());

await Task.Delay(2000);

namespace Partitional.ConsoleApp
{
    public sealed class Tester(ILogger<Tester> logger, IPartRepository repository)
    {
        public async Task ShouldRunTest(IPartitionManager partition)
        {
            logger.LogInformation("starting test");
            await partition.Migrate();
            var parts = await repository.GetPartsToDate("customer", DateTime.Now.AddDays(3));

            logger.LogInformation($"list of parts:{Environment.NewLine}{parts.Select(c => c.Id).JoinByString(Environment.NewLine)}");
        }
    }
}