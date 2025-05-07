using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

public interface IDownloadInterceptor
{
    ValueTask<bool> CanDownloadAsync(IFileStorage storage, string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken);
    ValueTask AfterDownloadAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken);
    ValueTask OnDownloadErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken);
}
