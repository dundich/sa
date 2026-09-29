using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// Registration-level behaviour of the S3 provider. The shared <c>IS3BucketClient</c> is
/// first-wins, so a second registration cannot be honoured — it has to be rejected rather than
/// silently ignored.
/// </summary>
public sealed class S3FileStorageRegistrationTests
{
    private static S3FileStorageOptions Options(
        string bucket = "mybucket",
        string endpoint = "http://localhost:9000",
        string basket = S3FileStorageOptions.DefaultBasket,
        string storageType = "s3",
        string accessKey = "ROOTUSER",
        string secretKey = "ChangeMe123") => new()
        {
            AccessKey = accessKey,
            SecretKey = secretKey,
            Bucket = bucket,
            Endpoint = endpoint,
            Basket = basket,
            StorageType = storageType,
        };

    // ---------- null / validation ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaS3FileStorage(Options()));
    }

    [Fact]
    public void Register_Rejects_NullOptions()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddSaS3FileStorage(null!));
    }

    [Fact]
    public void Register_Rejects_BlankEndpoint_BeforeTouchingTheClient()
    {
        // Used to surface as a bare UriFormatException from inside the bucket client setup,
        // or — with the old ArgumentNullException guard — as "Value cannot be null" for a blank.
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddSaS3FileStorage(Options(endpoint: "  ")));
        Assert.Equal("options.Endpoint", ex.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void Register_Rejects_NonAbsoluteEndpoint()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddSaS3FileStorage(Options(endpoint: "localhost:9000")));
        Assert.Equal("options.Endpoint", ex.ParamName);
    }

    [Fact]
    public void Register_Rejects_MalformedBasketAndStorageType()
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddSaS3FileStorage(Options(basket: "ab")));

        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddSaS3FileStorage(Options(storageType: "s3://")));
        Assert.Equal("options.StorageType", ex.ParamName);
    }

    // ---------- idempotency guard ----------

    [Fact]
    public void Register_IsIdempotent_ForEqualOptions()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());
        int after = services.Count;

        services.AddSaS3FileStorage(Options());

        Assert.Equal(after, services.Count);
    }

    [Fact]
    public void Register_Throws_ForADifferentBucket()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaS3FileStorage(Options(bucket: "other")));
        Assert.Contains("first-wins", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_ForADifferentEndpoint()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        Assert.Throws<InvalidOperationException>(() =>
            services.AddSaS3FileStorage(Options(endpoint: "http://other:9000")));
    }

    [Fact]
    public void Register_Throws_ForADifferentRegion()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        Assert.Throws<InvalidOperationException>(() =>
            services.AddSaS3FileStorage(Options() with { Region = "us-east-1" }));
    }

    [Fact]
    public void Register_Throws_ForADifferentBasket()
    {
        // The old guard compared only the client settings, so this was a silent no-op: the caller
        // got the first registration's basket while observing that the call "succeeded".
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaS3FileStorage(Options(basket: "uploads")));
        Assert.Contains("basket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_ForADifferentStorageType()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaS3FileStorage(Options(storageType: "minio")));
        Assert.Contains("storage type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Ignores_CredentialDrift()
    {
        // Rotating credentials must not fail an otherwise-correct startup: the client is already
        // built and cannot be rebuilt, so failing here would only block a redeploy.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());
        int after = services.Count;

        services.AddSaS3FileStorage(Options(accessKey: "OTHERUSER", secretKey: "OtherSecret456"));

        Assert.Equal(after, services.Count);
    }

    [Fact]
    public void Register_Ignores_ClientSettingsDrift()
    {
        // Transport tuning is applied once, when the shared client is built; a differing value
        // cannot take effect, so it must not block startup either.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());
        int after = services.Count;

        services.AddSaS3FileStorage(Options() with
        {
            ClientSettings = new Sa.Data.S3.S3BucketClientSetupSettings
            {
                AccessKey = "ROOTUSER",
                SecretKey = "ChangeMe123",
                Bucket = "mybucket",
                Endpoint = "http://localhost:9000",
                TotalRequestTimeout = TimeSpan.FromSeconds(5),
            },
        });

        Assert.Equal(after, services.Count);
    }

    // ---------- what actually gets registered ----------

    [Fact]
    public void Register_AddsExactlyOneFileStorage()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    [Fact]
    public void Register_AddsExactlyOneBucketClient()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());
        int after = services.Count;

        services.AddSaS3FileStorage(Options());

        // A repeated call must not register a second AddHttpClient pipeline: the first-wins
        // rule then depends on registration order rather than on the guard.
        Assert.Equal(after, services.Count);
    }

    [Fact]
    public void Register_NormalisesTheTrailingSlashOfTheEndpoint()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options(endpoint: "http://localhost:9000/"));

        var marker = Assert.IsType<S3FileStorageRegistration>(
            Assert.Single(services, d => d.ServiceType == typeof(S3FileStorageRegistration)).ImplementationInstance);

        Assert.Equal("http://localhost:9000", marker.Client.Endpoint);
    }

    [Fact]
    public void Register_ResolvesAStorageWithTheConfiguredNames()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options(basket: "uploads", storageType: "minio"));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("minio", storage.StorageType);
        Assert.Equal("uploads", storage.Basket);
        Assert.False(storage.IsReadOnly);
    }

    [Fact]
    public void Register_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Options());

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Register_RespectsAPreviouslyRegisteredTimeProvider()
    {
        var fake = new TimeProviderStub();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSaS3FileStorage(Options());

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }

    private sealed class TimeProviderStub : TimeProvider;
}
