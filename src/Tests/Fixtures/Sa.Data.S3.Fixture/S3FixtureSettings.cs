using Testcontainers.Minio;

namespace Sa.Data.S3.Fixture;

public record S3FixtureSettings
{
    public string BucketName { get; set; } = "test";
    public string DockerImage { get; set; } = "minio/minio:RELEASE.2025-04-08T15-41-24Z";
    public ushort MinioInternalPort { get; set; } = 9000;
    public Action<MinioBuilder>? Configure { get; set; }

    public readonly static S3FixtureSettings Instance = new();
}
