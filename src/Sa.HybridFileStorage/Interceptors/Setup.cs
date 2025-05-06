namespace Sa.HybridFileStorage.Interceptors;

internal static class Setup
{
    internal static IInterceptorContainer AddLoggingInterceptor(this IInterceptorContainer container, LoggingInterceptor? loggingInterceptor = null)
    {
        if (loggingInterceptor != null)
        {
            container.AddDeleteInterceptor(loggingInterceptor);
            container.AddDownloadInterceptor(loggingInterceptor);
            container.AddUploadInterceptor(loggingInterceptor);
        }
        return container;
    }
}
