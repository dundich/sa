namespace Sa.HybridFileStorage.Domain;

public record StorageResult(string FileId, string StorageType, DateTimeOffset UploadedAt);
