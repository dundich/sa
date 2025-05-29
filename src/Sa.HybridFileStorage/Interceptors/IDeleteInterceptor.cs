using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

public interface IDeleteInterceptor
{
    ValueTask<bool> CanDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken);
    ValueTask AfterDeleteAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken);
    ValueTask OnDeleteErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken);
}
