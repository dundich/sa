using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

public interface IFFMpegExecutor
{
    Task<string> GetVersion(CancellationToken cancellationToken = default);
    Task<string> GetFormats(CancellationToken cancellationToken = default);
    Task<string> GetCodecs(CancellationToken cancellationToken = default);

    Task<string> ConvertToPcmS16Le(
        string inputFileName,
        string outputFileName,
        int? targetSampleRate = null,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default);

    Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default);

    Task<string> ConvertToOgg(
        string inputFileName,
        string outputFileName,
        bool isLibopus = false,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default);

    public static IFFMpegExecutor Default { get; } = new FFMpegExecutorFactory().CreateFFMpegExecutor();
}
