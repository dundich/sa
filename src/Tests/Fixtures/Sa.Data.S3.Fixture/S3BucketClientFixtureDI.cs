using Microsoft.Extensions.DependencyInjection;
using Sa.Data;

namespace Sa.Data.S3.Fixture;


public class S3BucketClientFixtureDI : S3Fixture<IS3BucketClient>
{
    public S3BucketClientFixtureDI()
        : base()
    {
        SetupServices = (services, cfg)
            => services.AddS3BucketClientAsSingleton(CreateSettings("mybucket"));
    }

    public async override ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await ServiceProvider.GetRequiredService<IS3BucketClient>().CreateBucket(CancellationToken.None);
    }
}

