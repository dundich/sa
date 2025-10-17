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
    IFFRawExtecutor Extecutor { get; }

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
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Converts the input audio stream to PCM S16 LE (16-bit signed integer, little-endian) 
    /// </summary>
    /// <param name="inputStream">Input audio stream (must be readable).</param>
    /// <param name="inputFormat">Input format (e.g. "mp3", "wav", "flac").</param>
    /// <param name="onOutput">A callback that receives the resulting audio stream in WAV format with PCM S16 LE data.</param>
    /// <param name="outputSampleRate">Target sample rate (default: 16000 Hz). Use null to preserve original.</param>
    /// <param name="outputChannelCount">Number of output channels (e.g. 1 for mono). Use null to preserve original layout.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous conversion operation.</returns>
    /// <exception cref="ProcessStartException">Thrown when FFmpeg process fails to start.</exception>
    /// <exception cref="ProcessExecutionException">Thrown when FFmpeg returns a non-zero exit code (e.g. unsupported format, decoding error).</exception>
    /// <exception cref="ArgumentException">Thrown if required parameters are invalid (e.g. null stream, empty format).</exception>
    Task ConvertToPcmS16Le(
            Stream inputStream,
            string inputFormat,
            Func<Stream, CancellationToken, Task> onOutput,
            int? outputSampleRate = 16000,
            ushort? outputChannelCount = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default);

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
        bool isOverwrite = true,
        TimeSpan? timeout = null,
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
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    public static IFFMpegExecutor Default { get; } = new FFMpegExecutorFactory().CreateFFMpegExecutor();
}
