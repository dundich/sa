using Microsoft.Extensions.DependencyInjection;
using Sa.Data;

namespace Sa.Data.S3.Fixture;


public class S3BucketClientFixtureDI : S3Fixture<IS3BucketClient>
{
    public S3BucketClientFixtureDI()
        : base()
    {
        SetupServices = (services, cfg) =>
        {
            var settings = CreateSettings("mybucket");

            services.AddSaS3BucketClient(o => o.Options(ob => ob.Configure(x =>
            {
                x.Bucket = settings.Bucket;
                x.Endpoint = settings.Endpoint;
                x.AccessKey = settings.AccessKey;
                x.SecretKey = settings.SecretKey;
            })));
        };
    }

    public async override ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await ServiceProvider.GetRequiredService<IS3BucketClient>().CreateBucket(CancellationToken.None);
    }
}

