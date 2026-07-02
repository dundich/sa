using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Logs download-related operations across file storage providers.
/// </summary>
internal sealed partial class DownloadLoggingInterceptor(ILogger<DownloadLoggingInterceptor>? logger = null) : IDownloadInterceptor
{
    private readonly ILogger _logger = logger ?? NullLogger<DownloadLoggingInterceptor>.Instance;

    public ValueTask<bool> CanDownloadAsync(
        IFileStorage storage,
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        LogCanDownload(_logger, fileId, storage.StorageType);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterDownloadAsync(
        IFileStorage storage,
        string fileId,
        bool success,
        CancellationToken cancellationToken)
    {
        if (success)
        {
            LogDownloadSuccess(_logger, fileId, storage.StorageType);
        }
        else
        {
            LogDownloadFailure(_logger, fileId, storage.StorageType);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnDownloadErrorAsync(
        IFileStorage storage,
        string fileId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        LogDownloadError(_logger, exception, fileId, storage.StorageType);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Trace,
        Message = "Checking if can download file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogCanDownload(ILogger logger, string fileId, string storage);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Information,
        Message = "Successfully downloaded file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogDownloadSuccess(ILogger logger, string fileId, string storage);

    [LoggerMessage(
        EventId = 2303,
        Level = LogLevel.Warning,
        Message = "Failed to download file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogDownloadFailure(ILogger logger, string fileId, string storage);

    [LoggerMessage(
        EventId = 2304,
        Level = LogLevel.Error,
        Message = "Error occurred while downloading file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogDownloadError(ILogger logger, Exception ex, string fileId, string storage);
}
