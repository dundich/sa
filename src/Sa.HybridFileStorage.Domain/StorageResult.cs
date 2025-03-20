namespace Sa.HybridFileStorage.Domain;

public record StorageResult(string FileId, bool Success, string StorageType, DateTimeOffset UploadedAt);
