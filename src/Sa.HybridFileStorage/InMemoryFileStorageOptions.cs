namespace Sa.HybridFileStorage;

public sealed record InMemoryFileStorageOptions(string ScopeName, bool IsReadOnly = false);
