namespace Sa.Data.S3;

public sealed class S3BucketClientSetupSettings: S3BucketSettings
{
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(180);
}