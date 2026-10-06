using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sa.Schedule;
using Schedule.Configuration.Console;

// A schedule whose jobs are defined in code and then re-timed / disabled from
// appsettings.json. `AddSaSchedule` binds `ScheduleOptions` from the "Schedule"
// section, so the three jobs below keep their code defaults UNLESS the JSON
// overrides them — which it does for all three. Configuration wins over code.

var host = Host.CreateDefaultBuilder(args)
    .UseConsoleLifetime()
    .ConfigureServices((_, services) =>
    {
        services.AddSaSchedule(
            configure: builder =>
            {
                builder.UseHostedService();

                // Code defaults. The "Schedule:Jobs" section in appsettings.json
                // overrides each of these — no code change is needed to do so.
                builder.AddJob<HeartbeatJob>()
                    .WithName("Heartbeat")
                    .EverySeconds(30);

                builder.AddJob<SlowReportJob>()
                    .WithName("SlowReport")
                    .EverySeconds(2);

                builder.AddJob<NightlyCleanupJob>()
                    .WithName("NightlyCleanup")
                    .EveryMinutes(15);
            },
            configSectionPath: "Schedule");
    })
    .Build();

// The bound options (what appsettings.json actually says) — printed so the
// override is visible without waiting for a job to fire. Only the fields the
// config actually sets are shown: a field absent here was left to the code.
var options = host.Services.GetRequiredService<IOptions<ScheduleOptions>>().Value;
Console.WriteLine("appsettings.json (Schedule:Jobs) — the code defaults above are overridden:");
foreach (var (name, job) in options.Jobs)
{
    var fields = new List<string>();

    if (job.Disabled is not null)
    {
        fields.Add($"Disabled={job.Disabled}");
    }

    if (job.Immediate is not null)
    {
        fields.Add($"Immediate={job.Immediate}");
    }

    if (job.IsRunOnce is not null)
    {
        fields.Add($"IsRunOnce={job.IsRunOnce}");
    }

    if (job.Every is not null)
    {
        fields.Add($"Every={job.Every}");
    }

    if (job.Cron is not null)
    {
        fields.Add($"Cron={job.Cron}");
    }

    if (job.InitialDelay is not null)
    {
        fields.Add($"InitialDelay={job.InitialDelay}");
    }

    if (job.ConcurrencyLimit is not null)
    {
        fields.Add($"ConcurrencyLimit={job.ConcurrencyLimit}");
    }

    if (job.MaxConcurrency is not null)
    {
        fields.Add($"MaxConcurrency={job.MaxConcurrency}");
    }

    Console.WriteLine(fields.Count == 0
        ? $"  {name,-16} (nothing set — the code settings stand as-is)"
        : $"  {name,-16} {string.Join("  ", fields)}");
}

Console.WriteLine();
Console.WriteLine("Watch the next few seconds:");
Console.WriteLine("  - Heartbeat now runs every 2s (config), not every 30s (code), and fires at startup — Immediate=true in config.");
Console.WriteLine("  - SlowReport is silent (Disabled=true in config), not every 2s (code).");
Console.WriteLine("  - NightlyCleanup is now cron '0 3 * * *' (config), so it will not fire in this window.");
Console.WriteLine();

// Run for a fixed window so the sample is self-terminating; Ctrl+C stops early.
using var cts = new CancellationTokenSource();
var run = host.RunAsync(cts.Token);
try
{
    await Task.Delay(12_000, cts.Token);
}
catch (OperationCanceledException)
{
}

cts.Cancel();
await run;
Console.WriteLine("*** THE END ***");

// --- Jobs -------------------------------------------------------------------

namespace Schedule.Configuration.Console
{
    public sealed class HeartbeatJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            System.Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Heartbeat ran");
            return Task.CompletedTask;
        }
    }

    public sealed class SlowReportJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            // This line should never print — the job is disabled in appsettings.json.
            System.Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SlowReport ran (unexpected — it is disabled in config)");
            return Task.CompletedTask;
        }
    }

    public sealed class NightlyCleanupJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            System.Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] NightlyCleanup ran");
            return Task.CompletedTask;
        }
    }
}
