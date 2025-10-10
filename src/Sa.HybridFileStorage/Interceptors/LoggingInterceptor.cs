using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

internal sealed partial class LoggingInterceptor(ILogger<LoggingInterceptor>? logger = null) 
    : IDeleteInterceptor, IDownloadInterceptor, IUploadInterceptor
{
    private readonly ILogger _logger = logger ?? NullLogger<LoggingInterceptor>.Instance;

    public ValueTask<bool> CanDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken)
    {
        LogCanDelete(_logger, fileId, storage.StorageType);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterDeleteAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
    {
        if (success)
        {
            LogDeleteSuccess(_logger, fileId, storage.StorageType);
        }
        else
        {
            LogDeleteFailure(_logger, fileId, storage.StorageType);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDeleteErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
    {
        LogDeleteError(_logger, exception, fileId, storage.StorageType);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CanDownloadAsync(IFileStorage storage, string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        LogCanDownload(_logger, fileId, storage.StorageType);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterDownloadAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
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

    public ValueTask OnDownloadErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
    {
        LogDownloadError(_logger, exception, fileId, storage.StorageType);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
    {
        LogCanUpload(_logger, input, storage.StorageType);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken)
    {
        LogUploadSuccess(_logger, storage.StorageType, result);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken)
    {
        LogUploadError(_logger, exception, storage.StorageType);
        return ValueTask.CompletedTask;
    }


    [LoggerMessage(
        EventId = 2201, 
        Level = LogLevel.Trace, 
        Message = "Checking if can delete file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogCanDelete(ILogger logger, string fileId, string storage);
    
    [LoggerMessage(
        EventId = 2202, 
        Level = LogLevel.Information, 
        Message = "Successfully deleted file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogDeleteSuccess(ILogger logger, string fileId, string storage);
    
    [LoggerMessage(
        EventId = 2203, 
        Level = LogLevel.Warning, 
        Message = "Failed to delete file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogDeleteFailure(ILogger logger, string fileId, string storage);
    
    [LoggerMessage(
        EventId = 2204, 
        Level = LogLevel.Error, 
        Message = "Error occurred while deleting file with ID: `{FileId}` from storage: {Storage}")]
    static partial void LogDeleteError(ILogger logger, Exception ex, string fileId, string storage);

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

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Trace,
        Message = "Checking if can upload file: {Input} to storage: {Storage}")]
    static partial void LogCanUpload(ILogger logger, UploadFileInput input, string storage);

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Information,
        Message = "Successfully uploaded file to storage: {Storage} with result: {Result}")]
    static partial void LogUploadSuccess(ILogger logger, string storage, StorageResult result);

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Error,
        Message = "Error occurred while uploading file to storage: {Storage}")]
    static partial void LogUploadError(ILogger logger, Exception ex, string storage);
}
