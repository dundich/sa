namespace Sa.Data.S3;

public sealed class S3BucketClientSettings
{
    public required string Endpoint { get; set; }

    public required string Bucket { get; set; }

    public required string AccessKey { get; set; }

    public required string SecretKey { get; set; }

    public string Region { get; set; } = "eu-central-1";

    public string Service { get; set; } = "s3";

    public bool UseHttp2 { get; set; } = false;

    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(180);
}