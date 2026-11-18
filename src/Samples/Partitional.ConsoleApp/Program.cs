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
    .AddSaPostgreSqlDataSource(builder => builder.Options(ob => ob.Configure(o => o.ConnectionString = connectionString)))
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
            .AddMigrationChildren("RU", "Moscow", "Samara")
            .AddMigrationChildren("USA", "Alabama", "New York")
            .AddMigrationChildren("FR", "Paris", "Lyon", "Bordeaux");
        });


    })
    // Schedule for creating new partitions
    .AddPartMigrationSchedule(b => b.Configure((sp, opts) =>
    {
        opts.AsBackgroundJob = true;
        opts.ExecutionInterval = TimeSpan.FromHours(2);
        opts.ForwardDays = 2;
    }))
    // Schedule for removing old partitions
    .AddPartCleanupSchedule(b => b.Configure((sp, opts) =>
    {
        opts.AsBackgroundJob = true;
        opts.DropPartsAfterRetention = TimeSpan.FromDays(21);
    }));


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
            await partition.MigrateAsync();
            var parts = await repository.GetPartsToDate("customer", DateTime.Now.AddDays(3));

            if (logger.IsEnabled(LogLevel.Information))
            {
#pragma warning disable CA1873 // The list is built only when the level is enabled.
                SampleLog.PartsListed(logger, string.Join(Environment.NewLine, parts.Select(c => c.Id)));
#pragma warning restore CA1873
            }
            SampleLog.PartsLoaded(logger, parts.Count > 0);
        }
    }
}

internal static partial class SampleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "list of parts:\n{Parts}")]
    public static partial void PartsListed(ILogger logger, string parts);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Successfully: {Ok}")]
    public static partial void PartsLoaded(ILogger logger, bool ok);
}
