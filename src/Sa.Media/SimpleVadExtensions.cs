namespace Sa.Media;

public static class SimpleVadExtensions
{
    public static async Task<SimpleVad.Result> AnalyzeVoiceAsync(
        this SimpleVad vad,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        await using var reader = AsyncWavReader.CreateFromFile(audioPath);
        return await vad.AnalyzeVoiceAsync(reader, cancellationToken);
    }
}
