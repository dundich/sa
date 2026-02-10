using Sa.Data.S3.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

public class S3FileStorageFixture : S3Fixture<IFileStorage>
{
    public S3FileStorageFixture()
        : base()
    {
        SetupServices = (services, cfg)
            => services.AddSaS3FileStorage(CreateOptions());
    }

    private S3FileStorageOptions CreateOptions()
    {
        var settings = CreateSettings("mybucket");

        return new S3FileStorageOptions
        {
            AccessKey = settings.AccessKey,
            SecretKey = settings.SecretKey,
            Bucket = settings.Bucket,
            Endpoint = settings.Endpoint,
        };
    }
}
