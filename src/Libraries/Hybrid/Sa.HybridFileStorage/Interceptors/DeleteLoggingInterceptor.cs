using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Logs delete-related operations across file storage providers.
/// </summary>
internal sealed partial class DeleteLoggingInterceptor(ILogger<DeleteLoggingInterceptor>? logger = null) : IDeleteInterceptor
{
    private readonly ILogger _logger = logger ?? NullLogger<DeleteLoggingInterceptor>.Instance;

    public ValueTask<bool> CanDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken)
    {
        LogCanDelete(_logger, fileId, storage.StorageType);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterDeleteAsync(
        IFileStorage storage,
        string fileId,
        bool success,
        CancellationToken cancellationToken)
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

    public ValueTask OnDeleteErrorAsync(
        IFileStorage storage,
        string fileId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        LogDeleteError(_logger, exception, fileId, storage.StorageType);
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
}
