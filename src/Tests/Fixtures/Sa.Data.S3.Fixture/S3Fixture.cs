using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Sa.Fixture;

namespace Sa.Data.S3.Fixture;

public class S3Fixture<TSub> : SaFixture<TSub, S3FixtureSettings>
    where TSub : notnull
{
    public IContainer Container { get; private set; }

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


    public static ContainerBuilder CreateBuilder(S3FixtureSettings settings)
    {
        // Credentials are passed as AWS_* environment variables: with no S3 config file present the
        // gateway turns them into an admin identity, so every request has to be signed and buckets
        // are created on upload.
        return new ContainerBuilder(settings.DockerImage)
            .WithCommand(settings.Command)
            .WithEnvironment("AWS_ACCESS_KEY_ID", settings.AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", settings.SecretKey)
            .WithPortBinding(settings.S3InternalPort, true)
            // The gateway answers every anonymous request with 403, so a TCP probe is the reliable
            // readiness signal; an HTTP probe would have to accept a non-2xx status.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(settings.S3InternalPort))
            ;
    }

    public S3BucketClientSetupOptions CreateSettings(string bucket)
    {
        return new S3BucketClientSetupOptions
        {
            Bucket = bucket,
            Endpoint = $"http://{Container.Hostname}:{Container.GetMappedPublicPort(Settings.S3InternalPort)}",
            AccessKey = Settings.AccessKey,
            SecretKey = Settings.SecretKey
        };
    }
}
