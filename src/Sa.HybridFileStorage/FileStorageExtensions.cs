using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

internal static class FileStorageExtensions
{
    public static IEnumerable<IFileStorage> GetScopeStorages(
        this IReadOnlyCollection<IFileStorage> storages,
        string fileId,
        string? scopeName)
        => storages.Where(c => c.ScopeName == scopeName && c.CanProcess(fileId));
}
