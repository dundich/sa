using Sa.Data.S3.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// Two S3 storages of one basket ("share") backed by two buckets on the same Minio container:
/// the failover pair the multi-instance stage is for. Both storages answer <c>CanProcess</c> for
/// the same "s3://share/..." scheme, so a read probes the first storage (bucket-a) and continues
/// to the second (bucket-b) on a miss, while an upload is first-wins and lands in bucket-a.
/// </summary>
public sealed class S3TwoBucketsFixture : S3Fixture<IHybridFileStorage>
{
    public S3TwoBucketsFixture()
        : base()
    {
        SetupServices = (services, cfg) =>
        {
            var first = CreateSettings("bucket-a");
            var second = CreateSettings("bucket-b");

            services.AddSaS3FileStorage(o => o.Options(ob => ob.Configure(x =>
            {
                x.AccessKey = first.AccessKey;
                x.SecretKey = first.SecretKey;
                x.Bucket = first.Bucket;
                x.Endpoint = first.Endpoint;
            })));

            services.AddSaS3FileStorage(o => o.Options(ob => ob.Configure(x =>
            {
                x.AccessKey = second.AccessKey;
                x.SecretKey = second.SecretKey;
                x.Bucket = second.Bucket;
                x.Endpoint = second.Endpoint;
            })));

            services.AddSaHybridFileStorage();
        };
    }
}