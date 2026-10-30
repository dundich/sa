using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Defines an interceptor that can observe and react to file deletion operations.
/// </summary>
public interface IDeleteInterceptor
{
    /// <summary>
    /// Determines whether the specified file can be deleted by this storage provider.
    /// </summary>
    /// <param name="storage">The storage provider attempting the deletion.</param>
    /// <param name="fileId">The unique identifier of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns><c>true</c> if the file can be deleted; otherwise, <c>false</c>.</returns>
    ValueTask<bool> CanDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Called after a delete operation completes, regardless of success or failure.
    /// </summary>
    /// <param name="storage">The storage provider that performed the delete.</param>
    /// <param name="fileId">The unique identifier of the file that was deleted.</param>
    /// <param name="success"><c>true</c> if the delete operation succeeded; otherwise, <c>false</c>.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    ValueTask AfterDeleteAsync(
        IFileStorage storage,
        string fileId,
        bool success,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called when a delete operation throws an exception.
    /// </summary>
    /// <param name="storage">The storage provider that encountered the error.</param>
    /// <param name="fileId">The unique identifier of the file that caused the error.</param>
    /// <param name="exception">The exception thrown by the delete operation.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    ValueTask OnDeleteErrorAsync(IFileStorage storage,
        string fileId,
        Exception exception,
        CancellationToken cancellationToken);
}
