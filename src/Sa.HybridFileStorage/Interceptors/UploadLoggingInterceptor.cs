using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Logs upload-related operations across file storage providers.
/// </summary>
internal sealed partial class UploadLoggingInterceptor(ILogger<UploadLoggingInterceptor>? logger = null) : IUploadInterceptor
{
    private readonly ILogger _logger = logger ?? NullLogger<UploadLoggingInterceptor>.Instance;

    public ValueTask<bool> CanUploadAsync(
        IFileStorage storage,
        UploadFileInput input,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        LogCanUpload(_logger, input, storage.StorageType);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterUploadAsync(
        IFileStorage storage,
        StorageResult result,
        CancellationToken cancellationToken)
    {
        LogUploadSuccess(_logger, storage.StorageType, result);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnUploadErrorAsync(
        IFileStorage storage,
        Exception exception,
        CancellationToken cancellationToken)
    {
        LogUploadError(_logger, exception, storage.StorageType);
        return ValueTask.CompletedTask;
    }

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
