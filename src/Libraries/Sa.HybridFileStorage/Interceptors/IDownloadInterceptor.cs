using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Defines a contract for intercepting download operations in the hybrid file storage system.
/// </summary>
public interface IDownloadInterceptor
{
    /// <summary>
    /// Determines whether a download operation should proceed.
    /// </summary>
    /// <param name="storage">The storage provider initiating the download.</param>
    /// <param name="fileId">The unique identifier of the file to download.</param>
    /// <param name="loadStream">A function that processes the downloaded file stream.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns><c>true</c> to allow the download; otherwise, <c>false</c>.</returns>
    ValueTask<bool> CanDownloadAsync(
        IFileStorage storage,
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called after a download operation completes, regardless of success or failure.
    /// </summary>
    /// <param name="storage">The storage provider that performed the download.</param>
    /// <param name="fileId">The unique identifier of the downloaded file.</param>
    /// <param name="success"><c>true</c> if the download succeeded; otherwise, <c>false</c>.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    ValueTask AfterDownloadAsync(
        IFileStorage storage,
        string fileId,
        bool success,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called when a download operation throws an exception.
    /// </summary>
    /// <param name="storage">The storage provider that encountered the error.</param>
    /// <param name="fileId">The unique identifier of the file that caused the error.</param>
    /// <param name="exception">The exception thrown by the download operation.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    ValueTask OnDownloadErrorAsync(
        IFileStorage storage,
        string fileId,
        Exception exception,
        CancellationToken cancellationToken);
}
