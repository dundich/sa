using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StateMachine.Console;


Console.WriteLine("Hello, World!");

LongProcess lp = new();

var i = 0;
await foreach (var c in lp)
{
    Console.WriteLine($"{i++}. {c.CurrentState}");
}

// default configure...
IHostBuilder builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services =>
{
    services.AddLogging(builder => builder.AddConsole());
});

builder.UseConsoleLifetime();

var host = builder.Build();


IHostApplicationLifetime applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
await host.WaitForShutdownAsync(applicationLifetime.ApplicationStopping);

Console.WriteLine("*** THE END ***");
