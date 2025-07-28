namespace Sa.Media.FFmpeg.Services;

internal static class FFmpegChannelLayouts
{
    public static readonly IReadOnlyDictionary<int, string> LayoutByChannelCount = new Dictionary<int, string>
    {
        { 1, "mono" },
        { 2, "stereo" },
        { 3, "2.1" },
        { 4, "quad" },
        { 5, "4.0" },
        { 6, "5.1" },
        { 7, "5.1|downmix" },
        { 8, "7.1" },
    };

    public static string GetLayout(int channelCount) =>
        LayoutByChannelCount.TryGetValue(channelCount, out var layout)
            ? layout
            : "octagonal";
}
