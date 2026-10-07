using Microsoft.Extensions.DependencyInjection;
using Sa.Data;

namespace Sa.Data.S3.Fixture;


public class S3BucketClientFixtureDI : S3Fixture<IS3BucketClient>
{
    /// <summary>Имя именованного клиента этой фикстуры: резолв — только keyed под этим именем.</summary>
    public const string ClientName = "di-test-bucket";

    public S3BucketClientFixtureDI()
        : base()
    {
        SetupServices = (services, cfg) =>
        {
            var settings = CreateSettings("mybucket");

            services.AddSaS3BucketClient(ClientName, o => o.Options(ob => ob.Configure(x =>
            {
                x.Bucket = settings.Bucket;
                x.Endpoint = settings.Endpoint;
                x.AccessKey = settings.AccessKey;
                x.SecretKey = settings.SecretKey;
            })));
        };
    }

    /// <summary>
    /// Именованный клиент: unkeyed-резолва у именованной регистрации нет, поэтому базовое
    /// unkeyed-свойство <c>Sub</c> здесь перекрывается keyed-резолвом под именем клиента.
    /// </summary>
    public new IS3BucketClient Sub => ServiceProvider.GetRequiredKeyedService<IS3BucketClient>(ClientName);

    public async override ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Sub.CreateBucket(CancellationToken.None);
    }
}
