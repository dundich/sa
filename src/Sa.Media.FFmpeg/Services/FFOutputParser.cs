namespace Sa.Media.FFmpeg.Services;

static class FFOutputParser
{
    public static (int? channels, int? sampleRate) ParseChannelsAndSampleRate(string str)
    {

        ReadOnlySpan<char> inputSpan = str.AsSpan();
        int sampleRate = 0;
        int channels = 0;

        foreach (ReadOnlySpan<char> line in inputSpan.EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;

            var equalIndex = trimmed.IndexOf('=');
            if (equalIndex == -1) continue;

            var key = trimmed[..equalIndex].Trim();
            var value = trimmed[(equalIndex + 1)..].Trim();

            if (key.SequenceEqual("sample_rate".AsSpan()))
                _ = int.TryParse(value, out sampleRate);
            else if (key.SequenceEqual("channels".AsSpan()))
                _ = int.TryParse(value, out channels);
        }

        return (channels, sampleRate);
    }
}
