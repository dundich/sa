using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.Interceptors;


internal sealed class InterceptorContainer : IInterceptorContainer
{
    private readonly List<IUploadInterceptor> _uploadInterceptors = [];
    private readonly List<IDownloadInterceptor> _downloadInterceptors = [];
    private readonly List<IDeleteInterceptor> _deleteInterceptors = [];

    public IInterceptorContainer AddUploadInterceptor(IUploadInterceptor interceptor)
    {
        _uploadInterceptors.Add(interceptor);
        return this;
    }

    public IInterceptorContainer AddDownloadInterceptor(IDownloadInterceptor interceptor)
    {
        _downloadInterceptors.Add(interceptor);
        return this;
    }

    public IInterceptorContainer AddDeleteInterceptor(IDeleteInterceptor interceptor)
    {
        _deleteInterceptors.Add(interceptor);
        return this;
    }

    public async Task<bool> ExecuteBeforeUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _uploadInterceptors)
        {
            if (!await interceptor.CanUploadAsync(storage, input, fileStream, cancellationToken))
            {
                return false;
            }
        }
        return true;
    }

    public async Task ExecuteAfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _uploadInterceptors)
        {
            await interceptor.AfterUploadAsync(storage, result, cancellationToken);
        }
    }

    public async Task ExecuteOnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _uploadInterceptors)
        {
            await interceptor.OnUploadErrorAsync(storage, exception, cancellationToken);
        }
    }

    public async Task<bool> ExecuteBeforeDownloadAsync(IFileStorage storage, string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _downloadInterceptors)
        {
            if (!await interceptor.CanDownloadAsync(storage, fileId, loadStream, cancellationToken))
            {
                return false;
            }
        }
        return true;
    }

    public async Task ExecuteAfterDownloadAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _downloadInterceptors)
        {
            await interceptor.AfterDownloadAsync(storage, fileId, success, cancellationToken);
        }
    }

    public async Task ExecuteOnDownloadErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _downloadInterceptors)
        {
            await interceptor.OnDownloadErrorAsync(storage, fileId, exception, cancellationToken);
        }
    }

    public async Task<bool> ExecuteBeforeDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _deleteInterceptors)
        {
            if (!await interceptor.CanDeleteAsync(storage, fileId, cancellationToken))
            {
                return false;
            }
        }
        return true;
    }

    public async Task ExecuteAfterDeleteAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _deleteInterceptors)
        {
            await interceptor.AfterDeleteAsync(storage, fileId, success, cancellationToken);
        }
    }

    public async Task ExecuteOnDeleteErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _deleteInterceptors)
        {
            await interceptor.OnDeleteErrorAsync(storage, fileId, exception, cancellationToken);
        }
    }
}
