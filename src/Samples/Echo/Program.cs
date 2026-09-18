using Sa.Media.Echo;

const string wavPath = @"src\Tests\Sa.MediaTests\data\pcm_s16le.wav";

string outputPath = Path.GetRandomFileName() + ".wav";

if (!File.Exists(wavPath))
{
    Console.WriteLine("File not found {0}", wavPath);
    return;
}

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
    File.Delete(outputPath);
    Console.WriteLine("Result deleted.");
}
