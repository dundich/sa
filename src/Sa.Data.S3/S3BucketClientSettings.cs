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

    public S3BucketClientSettings() { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="uri">https://hostname.com:443/mybucket"</param>
    public static S3BucketClientSettings Create(Uri uri, string accessKey, string secretKey, string? bucket = null, string? region = null)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            string path = uri.AbsolutePath;
            if (!string.IsNullOrEmpty(path) || path != "/")
            {
                bucket = path.TrimStart('/').Split('/')[0];
            }
        }

        var settings = new S3BucketClientSettings
        {
            Endpoint = uri.AbsoluteUri,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Bucket = bucket ?? throw new ArgumentNullException(nameof(bucket))
        };

        if (!string.IsNullOrWhiteSpace(region)) settings.Region = region;

        return settings;
    }
}
