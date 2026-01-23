namespace Sa.HybridFileStorage.Interceptors;

public interface IInterceptorContainer
{
    IInterceptorContainer AddDeleteInterceptor(IDeleteInterceptor interceptor);
    IInterceptorContainer AddDownloadInterceptor(IDownloadInterceptor interceptor);
    IInterceptorContainer AddUploadInterceptor(IUploadInterceptor interceptor);
}
