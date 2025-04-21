using HybridFileStorage.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;
using System.Diagnostics;
using System.Text;


Console.WriteLine("Hello, HybridFileStorage!");

// default configure...
IHostBuilder builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services =>
{
    services.AddHybridStorage((sp, builder) =>
    {
        var tp = sp.GetRequiredService<ICurrentTimeProvider>();
        builder.AddStorage(new InMemoryFileStorage(tp));
    });

    services.AddLogging(builder => builder.AddConsole());

    services.TryAddSingleton<Proccessor>();
});

builder.UseConsoleLifetime();

var host = builder.Build();

await host.Services.GetRequiredService<Proccessor>().Run();


namespace HybridFileStorage.Console
{
    public class Proccessor(IHybridFileStorage storage, ILogger<Proccessor> logger)
    {
        public async Task Run(CancellationToken cancellationToken = default)
        {
            logger.LogInformation("starting");

            var expected = "Hello, HybridFileStorage!";
            using var stream = expected.ToStream();

            var result = await storage.UploadFileAsync(new UploadFileInput { FileName = "file.txt" }, stream, cancellationToken);

            string? actual = default;

            var isDowload = await storage.DownloadFileAsync(result.FileId, async (fs, t) => actual = await fs.ToStrAsync(t), cancellationToken);

            Debug.Assert(isDowload);

            System.Console.WriteLine("completed:{0}", actual ?? String.Empty);

            Debug.Assert(expected == actual);

            await Task.Delay(2000, cancellationToken);
        }
    }

    public static class StreamExtensions
    {
        public static Stream ToStream(this string input, Encoding? encoding = null)
        {
            encoding ??= Encoding.UTF8;
            byte[] byteArray = encoding.GetBytes(input);
            return new MemoryStream(byteArray);
        }

        public static string ToStr(this Stream stream)
        {
            stream.Position = 0;
            using StreamReader reader = new(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public async static Task<string> ToStrAsync(this Stream stream, CancellationToken cancellationToken)
        {
            stream.Position = 0;
            using StreamReader reader = new(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }
}
