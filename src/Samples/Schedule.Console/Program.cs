using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sa.Schedule;
using Schedule.Console;


Console.Write("Hello, Schedule! As host service (Y/n): ");

bool isHostService = Console.ReadKey().Key == ConsoleKey.Y;

Console.WriteLine();

// default configure...
IHostBuilder builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services =>
{
    services.AddSchedule(builder =>
    {
        if (isHostService) builder.UseHostedService();

        builder.AddJob<SomeJob>()
            .EverySeconds(2)
            .WithName("Some 2")
            .ConfigureErrorHandling(c => c.IfErrorRetry(2).ThenStopJob())
            ;

        builder.AddInterceptor<SomeInterceptor>();
    });

    services.AddLogging(builder => builder.AddConsole());
});

builder.UseConsoleLifetime();

var host = builder.Build();

var controller = host.Services.GetRequiredService<IScheduler>();

var cts = new CancellationTokenSource();

if (isHostService)
{
    _ = host.RunAsync(cts.Token);
}
else
{
    controller.Start(cts.Token);
}

_ = Task.Run(async () =>
{
    await Task.Delay(5000);
    await controller.Stop();
    Console.WriteLine($"*** stopped & restart after 2 sec");
    await Task.Delay(2000);
    controller.Restart();
});

_ = Task.Run(async () =>
{
    await Task.Delay(30000);
    await cts.CancelAsync();
    Console.WriteLine($"*** cancelled on timeout");
});


IHostApplicationLifetime applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
await host.WaitForShutdownAsync(applicationLifetime.ApplicationStopping);

cts.Dispose();
Console.WriteLine("*** THE END ***");



namespace Schedule.Console
{
    public class SomeJob(TimeProvider currentTime) : IJob
    {
        public async Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            if (context.NumIterations >= 4 && context.NumIterations < 6)
            {
                System.Console.WriteLine($"err {context.FailedIterations}");
                throw new ArgumentException("test");
            }

            System.Console.WriteLine($"{currentTime.GetUtcNow()} {context.NumIterations}: {context.JobName}");
        }
    }

    public class SomeInterceptor : IJobInterceptor
    {
        public async Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken)
        {
            System.Console.WriteLine($"<beg>");
            await next();
            System.Console.WriteLine($"<end>");
        }
    }
}