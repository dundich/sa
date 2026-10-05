using Sa.Data.S3.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

public class S3FileStorageFixture : S3Fixture<IFileStorage>
{
    public S3FileStorageFixture()
        : base()
    {
        SetupServices = (services, cfg) =>
        {
            var settings = CreateSettings("mybucket");

            services.AddSaS3FileStorage(o => o.Configure(x =>
            {
                x.AccessKey = settings.AccessKey;
                x.SecretKey = settings.SecretKey;
                x.Bucket = settings.Bucket;
                x.Endpoint = settings.Endpoint;
            }));
        };
    }
}
