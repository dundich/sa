using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Partitional.ConsoleApp;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql;


Console.WriteLine("Hello, Partitional.PostgreSql!");

var connectionString = "Host=localhost;Username=postgres;Password=postgres;Database=postgres";


var hostBuilder = Host.CreateApplicationBuilder(args);

hostBuilder.Services
    .AddSingleton<Tester>()
    .AddLogging(c => c.AddConsole())
    .AddSaPostgreSqlDataSource(builder => builder.WithConnectionString(connectionString))
    .AddSaPartitional((sp, builder) =>
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
        opts.AsBackgroundJob = true;
        opts.ExecutionInterval = TimeSpan.FromHours(2);
        opts.ForwardDays = 2;
    })
    // Schedule for removing old partitions
    .AddPartCleanupSchedule((sp, opts) =>
    {
        opts.AsBackgroundJob = true;
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
            await partition.Migrate();
            var parts = await repository.GetPartsToDate("customer", DateTime.Now.AddDays(3));

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation($"list of parts:{Environment.NewLine}{string.Join(Environment.NewLine, parts.Select(c => c.Id))}");
                logger.LogInformation("Successfully: {Ok}", parts.Count > 0);
            }
        }
    }
}
