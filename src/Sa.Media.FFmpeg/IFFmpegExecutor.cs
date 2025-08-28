using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

/// <summary>
/// Interface for executing FFmpeg commands to extract audio/video.
/// </summary>
public interface IFFMpegExecutor
{
    /// <summary>
    /// Gets the executor responsible for running FFmpeg commands in raw mode.
    /// </summary>
    IFFRawExteсutor Exteсutor { get; }

    /// <summary>
    /// Gets the version of FFmpeg by executing the <c>ffmpeg -version</c> command.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A string containing the FFmpeg version output.</returns>
    Task<string> GetVersion(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of supported formats by executing the <c>ffmpeg -formats</c> command.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A string containing the list of supported formats.</returns>
    Task<string> GetFormats(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of supported codecs by executing the <c>ffmpeg -codecs</c> command.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A string containing the list of supported codecs.</returns>
    Task<string> GetCodecs(CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts an audio file to PCM S16 LE format (16-bit signed integer, little-endian).
    /// </summary>
    /// <param name="inputFileName">Path to the input audio file.</param>
    /// <param name="outputFileName">Path to the output file.</param>
    /// <param name="outputSampleRate">Optional target sample rate.</param>
    /// <param name="outputChannelCount">Optional number of output channels.</param>
    /// <param name="isOverwrite">If true, overwrites the output file if it already exists.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Stdout for log</returns>
    Task<string> ConvertToPcmS16Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Converts an audio file to PCM S16 LE format (16-bit signed integer, little-endian).
    /// </summary>
    Stream ConvertToPcmS16Le(
        Stream inputStream,
        string inputFormat,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null);

    /// <summary>
    /// Converts an audio file to MP3 format.
    /// </summary>
    /// <param name="inputFileName">Path to the input audio file.</param>
    /// <param name="outputFileName">Path to the output file.</param>
    /// <param name="isOverwrite">If true, overwrites the output file if it already exists.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Stdout for log</returns>
    Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts an audio file to OGG format (Vorbis or Opus codec).
    /// </summary>
    /// <param name="inputFileName">Path to the input audio file.</param>
    /// <param name="outputFileName">Path to the output file.</param>
    /// <param name="isLibopus">If true, uses the Opus codec; otherwise uses Vorbis.</param>
    /// <param name="isOverwrite">If true, overwrites the output file if it already exists.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Stdout for log</returns>
    Task<string> ConvertToOgg(
        string inputFileName,
        string outputFileName,
        bool isLibopus = false,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default);

    public static IFFMpegExecutor Default { get; } = new FFMpegExecutorFactory().CreateFFMpegExecutor();
}
