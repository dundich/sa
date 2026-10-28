using Sa.Media.Echo;

// Путь можно передать аргументом; иначе берём тестовую запись из репозитория.
string wavPath = args.Length > 0
    ? args[0]
    : Path.Combine("src", "Tests", "Sa.MediaTests", "data", "pсmS16Le.wav");

if (!File.Exists(wavPath))
{
    Console.WriteLine($"File not found: {wavPath}");
    Console.WriteLine("Usage: dotnet run --project src/Samples/Echo -- <path-to-stereo-wav>");
    return;
}

string outputPath = Path.GetRandomFileName() + ".wav";

try
{
    await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
        InputPath: wavPath,
        OutputPath: outputPath), CancellationToken.None);

    Console.WriteLine($"Wrote suppressed output: {outputPath}");
    Console.WriteLine($"Output size: {new FileInfo(outputPath).Length} bytes");
}
finally
{
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
        Console.WriteLine("Result deleted.");
    }
}
