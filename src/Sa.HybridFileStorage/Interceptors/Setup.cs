namespace Sa.HybridFileStorage.Interceptors;

internal static class Setup
{
    /// <summary>
    /// Adds all logging interceptors (upload, download, delete) to the container.
    /// </summary>
    internal static IInterceptorContainer AddLoggingInterceptors(
        this IInterceptorContainer container,
        UploadLoggingInterceptor? uploadInterceptor = null,
        DownloadLoggingInterceptor? downloadInterceptor = null,
        DeleteLoggingInterceptor? deleteInterceptor = null)
    {
        if (uploadInterceptor != null)
            container.AddUploadInterceptor(uploadInterceptor);

        if (downloadInterceptor != null)
            container.AddDownloadInterceptor(downloadInterceptor);

        if (deleteInterceptor != null)
            container.AddDeleteInterceptor(deleteInterceptor);

        return container;
    }
}
