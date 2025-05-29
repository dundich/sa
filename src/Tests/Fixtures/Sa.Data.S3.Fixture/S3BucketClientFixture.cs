using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Data.S3.Fixture;

public class S3BucketClientFixture : S3Fixture<IS3BucketClient>
{
    public S3BucketClientFixture()
        : base()
    {
        SetupServices = (services, cfg)
            => services.TryAddSingleton<IS3BucketClient>(sp => CreateClient(Settings.BucketName));
    }

    public S3BucketClient CreateClient(string backetName) => new(new HttpClient(), CreateSettings(backetName));

    public async override ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Sub.CreateBucket(CancellationToken.None);
    }

    public HttpClient HttpClient { get; } = new HttpClient();
}
