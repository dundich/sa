namespace Sa.Data.S3;

public sealed class S3Settings
{
    public required bool UseHttps { get; init; }

    public required string Hostname { get; init; }

    public required string Bucket { get; init; }

    public int? Port { get; init; }

    public required string AccessKey { get; init; }

    public required string SecretKey { get; init; }

    public string Region { get; init; } = "us-east-1";

    public string Service { get; init; } = "s3";

    public bool UseHttp2 { get; init; } = false;

    public S3Settings() { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="uri">https://hostname.com:443/mybucket"</param>
    public S3Settings(Uri uri, string accessKey, string secretKey, string? bucket = null)
    {
        UseHttps = uri.Scheme == "https";
        Hostname = uri.Host;
        Port = uri.Port;

        if (string.IsNullOrWhiteSpace(bucket))
        {
            string path = uri.AbsolutePath;
            // Проверяем, есть ли путь
            if (!string.IsNullOrEmpty(path) || path != "/")
            {
                Bucket = path.TrimStart('/').Split('/')[0];
            }
        }
        else
        {
            Bucket = bucket;
        }

        AccessKey = accessKey;
        SecretKey = secretKey;
    }
}
