namespace Sa.HybridFileStorage;

/// <summary>
/// Configuration options for the in-memory file storage provider.
/// </summary>
/// <param name="basket">The default basket (container) name. Defaults to <c>"share"</c>.</param>
/// <param name="isReadOnly"><c>true</c> if the storage should reject write operations; otherwise, <c>false</c>.</param>
public sealed record InMemoryFileStorageOptions(string Basket = "share", bool IsReadOnly = false);
