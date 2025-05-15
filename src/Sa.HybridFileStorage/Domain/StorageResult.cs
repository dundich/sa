namespace Sa.HybridFileStorage.Domain;

public record StorageResult(string FileId, string AbsoluteUrl, string StorageType, DateTimeOffset UploadedAt);
