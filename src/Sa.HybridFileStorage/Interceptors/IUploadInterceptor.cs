using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Defines a contract for intercepting upload operations in the hybrid file storage system.
/// </summary>
public interface IUploadInterceptor
{
    /// <summary>
    /// Determines whether an upload operation should proceed.
    /// </summary>
    /// <param name="storage">The storage provider initiating the upload.</param>
    /// <param name="input">Metadata about the file being uploaded.</param>
    /// <param name="fileStream">A stream containing the file data to be uploaded.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns><c>true</c> to allow the upload; otherwise, <c>false</c>.</returns>
    ValueTask<bool> CanUploadAsync(
        IFileStorage storage,
        UploadFileInput input,
        Stream fileStream,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called after an upload operation completes, regardless of success or failure.
    /// </summary>
    /// <param name="storage">The storage provider that performed the upload.</param>
    /// <param name="result">The result of the upload operation.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Called when an upload operation throws an exception.
    /// </summary>
    /// <param name="storage">The storage provider that encountered the error.</param>
    /// <param name="exception">The exception thrown by the upload operation.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken);
}
