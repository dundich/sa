using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Data.S3.Fixture;

public class S3ClientFixture : S3Fixture<IS3Client>
{
    public S3ClientFixture()
        : base()
    {
        SetupServices = (services, cfg)
            => services.TryAddSingleton<IS3Client>(sp => CreateClient(Settings.BucketName));
    }

    private S3Settings CreateSettings(string bucket)
    {
        return new S3Settings
        {
            Bucket = bucket,
            Hostname = Container.Hostname,
            AccessKey = Container.GetAccessKey(),
            Port = Container.GetMappedPublicPort(Settings.MinioInternalPort),
            UseHttps = false,
            SecretKey = Container.GetSecretKey(),
        };
    }

    public S3Client CreateClient(string backetName) => new(CreateSettings(backetName));

    public async override ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Sub.CreateBucket(CancellationToken.None);
    }

    public HttpClient HttpClient { get; } = new HttpClient();
}
