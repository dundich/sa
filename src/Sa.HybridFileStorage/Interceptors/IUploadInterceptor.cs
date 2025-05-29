using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;

public interface IUploadInterceptor
{
    ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken);
    ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken);
    ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken);
}
