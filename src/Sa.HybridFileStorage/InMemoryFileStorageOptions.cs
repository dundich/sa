namespace Sa.HybridFileStorage;

public sealed record InMemoryFileStorageOptions(string? ScopeName = null, bool IsReadOnly = false);
