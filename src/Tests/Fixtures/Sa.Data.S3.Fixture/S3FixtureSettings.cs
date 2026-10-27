using DotNet.Testcontainers.Builders;

namespace Sa.Data.S3.Fixture;

public record S3FixtureSettings
{
    public string BucketName { get; set; } = "test";

    /// <summary>
    /// S3-compatible server image. SeaweedFS rather than MinIO: <c>minio/minio</c> no longer answers
    /// anonymous pulls on Docker Hub (<c>denied: requested access to the resource is denied</c>) and
    /// <c>quay.io/minio/minio</c>, MinIO's own registry, rejects them as well.
    /// </summary>
    public string DockerImage { get; set; } = "chrislusf/seaweedfs:4.50";

    /// <summary>
    /// Command handed to the image entrypoint. <c>mini</c> starts master, volume, filer and the S3
    /// gateway in a single process, which is what a per-test-class container needs.
    /// <c>s3.autoCreateBucket</c> is switched off so that writing to a missing bucket fails the way
    /// it does on MinIO (<c>NoSuchBucket</c>) instead of silently creating the bucket.
    /// </summary>
    public string[] Command { get; set; } = ["mini", "-dir=/data", "-s3.autoCreateBucket=false"];

    /// <summary>
    /// Port the S3 gateway listens on inside the container. Unlike MinIO's 9000 this is fixed by
    /// SeaweedFS' <c>mini</c> defaults.
    /// </summary>
    public ushort S3InternalPort { get; set; } = 8333;

    /// <summary>
    /// Credentials exported to the gateway as <c>AWS_ACCESS_KEY_ID</c> / <c>AWS_SECRET_ACCESS_KEY</c>.
    /// With no config file present they become an admin identity, so requests must be signed.
    /// </summary>
    public string AccessKey { get; set; } = "testkey";

    /// <inheritdoc cref="AccessKey"/>
    public string SecretKey { get; set; } = "testsecret";

    public Action<ContainerBuilder>? Configure { get; set; }

    public readonly static S3FixtureSettings Instance = new();
}
