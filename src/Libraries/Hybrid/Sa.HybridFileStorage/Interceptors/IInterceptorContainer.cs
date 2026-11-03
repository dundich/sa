namespace Sa.HybridFileStorage.Interceptors;

/// <summary>
/// Provides a container for registering interceptor implementations that hook into upload, download, and delete operations.
/// </summary>
public interface IInterceptorContainer
{
    /// <summary>
    /// Registers a delete interceptor.
    /// </summary>
    /// <param name="interceptor">The delete interceptor to register.</param>
    /// <returns>The same <see cref="IInterceptorContainer"/> instance for chaining.</returns>
    IInterceptorContainer AddDeleteInterceptor(IDeleteInterceptor interceptor);

    /// <summary>
    /// Registers a download interceptor.
    /// </summary>
    /// <param name="interceptor">The download interceptor to register.</param>
    /// <returns>The same <see cref="IInterceptorContainer"/> instance for chaining.</returns>
    IInterceptorContainer AddDownloadInterceptor(IDownloadInterceptor interceptor);

    /// <summary>
    /// Registers an upload interceptor.
    /// </summary>
    /// <param name="interceptor">The upload interceptor to register.</param>
    /// <returns>The same <see cref="IInterceptorContainer"/> instance for chaining.</returns>
    IInterceptorContainer AddUploadInterceptor(IUploadInterceptor interceptor);
}
