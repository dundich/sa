namespace Sa.Media.FFmpeg;

/// <summary>
/// Interface for splitting multi-channel PCM S16 LE audio into individual mono channel files.
/// </summary>
public interface IPcmS16LeChannelManipulator
{
    /// <summary>
    /// Splits a multi-channel PCM S16 LE audio file into separate mono files for each channel.
    /// </summary>
    /// <param name="inputFileName">Path to the input audio file.</param>
    /// <param name="outputFileName">Base path for the output files. Channel files will be named using this base plus a suffix.</param>
    /// <param name="outputSampleRate">Optional target sample rate for resampling during split.</param>
    /// <param name="channelSuffix">Suffix to append before the file extension to distinguish channel files (default: "_channel_").</param>
    /// <param name="isOverwrite">If true, overwrites existing files; otherwise skips if files exist.</param>
    /// <param name="timeout">Optional timeout for the operation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of paths to the generated mono channel files.</returns>
    Task<IReadOnlyList<string>> SplitAsync(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = null,
        string channelSuffix = "_channel_",
        bool isOverwrite = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Joins two mono audio files into a single stereo audio file.
    /// </summary>
    /// <param name="leftFileName">Path to the left channel audio file.</param>
    /// <param name="rightFileName">Path to the right channel audio file.</param>
    /// <param name="outputFileName">Path to the output stereo audio file.</param>
    /// <param name="outputSampleRate">Optional target sample rate for the output file.</param>
    /// <param name="isOverwrite">If true, overwrites the output file if it already exists.</param>
    /// <param name="timeout">Optional timeout for the operation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The result is the path to the joined stereo file.</returns>
    Task<string> JoinAsync(
        string leftFileName,
        string rightFileName,
        string outputFileName,
        int? outputSampleRate = null,
        bool isOverwrite = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
