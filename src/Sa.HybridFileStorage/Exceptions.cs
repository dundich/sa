using System.Diagnostics.CodeAnalysis;
using System.Security;

namespace Sa.HybridFileStorage;

public class HybridFileStorageNoAvailableException() : Exception("No storage available.");


public class HybridFileStorageAggregateException(IEnumerable<Exception> innerExceptions)
    : AggregateException("Operation failed for some available storages.", innerExceptions);


public class HybridFileStorageWritableException()
    : Exception("Cannot perform operation. All storage options are read-only.");



public static class HybridFileStorageThrowHelper
{
    [DoesNotReturn]
    public static void ThrowWritableException() =>
        throw new HybridFileStorageWritableException();

    [DoesNotReturn]
    public static void ThrowInvalidFileIdFormat() =>
        throw new FormatException("Invalid file ID format.");

    [DoesNotReturn]
    public static void ThrowSecurityException(string path, string basePath) =>
        throw new SecurityException(
            $"Access denied. Path '{path}' is outside the allowed base directory '{basePath}'.");
}
