using Sa.Fixture;
using Testcontainers.Minio;

namespace Sa.Data.S3.Fixture;

public class S3Fixture<TSub> : SaFixture<TSub, S3FixtureSettings>
    where TSub : notnull
{
    public MinioContainer Container { get; private set; }

    protected S3Fixture(S3FixtureSettings? settings = null)
        : base(settings ?? S3FixtureSettings.Instance)
    {
        var builder = CreateBuilder(Settings);
        Settings.Configure?.Invoke(builder);
        Container = builder.Build();
    }


    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Container.StartAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
        await base.DisposeAsync();
    }


    public static MinioBuilder CreateBuilder(S3FixtureSettings settings)
    {
        return new MinioBuilder()
            .WithImage(settings.DockerImage)
            .WithPortBinding(settings.MinioInternalPort, true)
            ;
    }
    public S3BucketClientSettings CreateSettings(string bucket)
    {
        return new S3BucketClientSettings
        {
            Bucket = bucket,
            Endpoint = $"http://{Container.Hostname}:{Container.GetMappedPublicPort(Settings.MinioInternalPort)}",
            AccessKey = Container.GetAccessKey(),
            SecretKey = Container.GetSecretKey()
        };
    }
}
