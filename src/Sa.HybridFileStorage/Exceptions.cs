using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security;

namespace Sa.HybridFileStorage;

/// <summary>
/// Thrown when no storage provider is available to perform the requested operation.
/// </summary>
public class HybridFileStorageNoAvailableException() : Exception("No storage available.");

/// <summary>
/// Thrown when an operation fails across multiple storage providers.
/// </summary>
public class HybridFileStorageAggregateException(IEnumerable<Exception> innerExceptions)
    : AggregateException("Operation failed for some available storages.", innerExceptions);

/// <summary>
/// Thrown when a write operation is attempted but all storage providers are read-only.
/// </summary>
public class HybridFileStorageWritableException()
    : Exception("Cannot perform operation. All storage options are read-only.");

/// <summary>
/// Provides static helper methods for throwing hybrid file storage exceptions.
/// </summary>
public static class HybridFileStorageThrowHelper
{
    /// <summary>
    /// Throws a <see cref="HybridFileStorageWritableException"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowWritableException() =>
        throw new HybridFileStorageWritableException();

    /// <summary>
    /// Throws a <see cref="FormatException"/> indicating an invalid file ID format.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowInvalidFileIdFormat() =>
        throw new FormatException("Invalid file ID format.");

    /// <summary>
    /// Throws a <see cref="SecurityException"/> indicating that access to the specified path outside the base directory was denied.
    /// </summary>
    /// <param name="path">The path that caused the security violation.</param>
    /// <param name="basePath">The allowed base directory.</param>
    [DoesNotReturn]
    public static void ThrowSecurityException(string path, string basePath) =>
        throw new SecurityException(
            $"Access denied. Path '{path}' is outside the allowed base directory '{basePath}'.");
}
