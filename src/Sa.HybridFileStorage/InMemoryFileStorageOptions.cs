namespace Sa.HybridFileStorage;

/// <summary>
/// Configuration options for the in-memory file storage provider.
/// </summary>
/// <param name="Basket">The default basket (container) name. Defaults to <c>"share"</c>.</param>
/// <param name="IsReadOnly"><c>true</c> if the storage should reject write operations; otherwise, <c>false</c>.</param>
/// <param name="MaxSizeBytes">Maximum total size in bytes for all stored files. Default is 1 GB (<c>1_073_741_824</c>). Set to zero or negative to disable the limit.</param>
public sealed record InMemoryFileStorageOptions(
    string Basket = "share",
    bool IsReadOnly = false,
    long MaxSizeBytes = 1_073_741_824);
