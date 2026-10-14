using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// Registration-level behaviour of the S3 provider: the options pipeline the caller configures, and
/// what one registration ends up putting in the container — including several storages in one
/// collection, each with its own named options instance and its own keyed bucket client.
/// </summary>
public sealed class S3FileStorageRegistrationTests
{
    private static Action<IS3FileStorageBuilder> Configure(
        string bucket = "mybucket",
        string endpoint = "http://localhost:9000",
        string basket = S3FileStorageOptions.DefaultBasket,
        string storageType = "s3",
        string accessKey = "ROOTUSER",
        string secretKey = "ChangeMe123")
        => b => b.Options(o => o.Configure(x =>
        {
            x.AccessKey = accessKey;
            x.SecretKey = secretKey;
            x.Bucket = bucket;
            x.Endpoint = endpoint;
            x.Basket = basket;
            x.StorageType = storageType;
        }));

    private static S3FileStorageOptions Options(IServiceProvider provider)
        => Options(provider, provider.GetServices<S3FileStorageRegistration>().Single());

    private static S3FileStorageOptions Options(IServiceProvider provider, S3FileStorageRegistration registration)
        => provider.GetRequiredService<IOptionsMonitor<S3FileStorageOptions>>().Get(registration.OptionsName);

    private static S3BucketClient Client(IServiceProvider provider, S3FileStorageRegistration registration)
        => (S3BucketClient)provider.GetRequiredKeyedService<IS3BucketClient>(registration.OptionsName);

    // ---------- null / повторная регистрация ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaS3FileStorage());
    }

    [Fact]
    public void Register_TwoStorages_EachGetTheirOwnOptionsAndClient()
    {
        // Two S3 storages of one basket, backed by different buckets: the failover pair the
        // multi-instance stage is for. Each registration owns its named options instance and its
        // keyed bucket client, so no settings ever merge between them.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(bucket: "bucket-a"));
        services.AddSaS3FileStorage(Configure(bucket: "bucket-b"));

        using var provider = services.BuildServiceProvider();

        var registrations = provider.GetServices<S3FileStorageRegistration>().ToArray();
        Assert.Equal(2, registrations.Length);
        Assert.NotEqual(registrations[0].OptionsName, registrations[1].OptionsName);

        var storages = provider.GetServices<IFileStorage>().ToArray();
        Assert.Equal(2, storages.Length);
        Assert.NotSame(storages[0], storages[1]);
        Assert.Equal("share", storages[0].Basket);
        Assert.Equal("share", storages[1].Basket);

        var clientA = Client(provider, registrations[0]);
        var clientB = Client(provider, registrations[1]);

        Assert.Equal("bucket-a", clientA.Bucket);
        Assert.Equal("bucket-b", clientB.Bucket);
        Assert.NotSame(clientA, clientB);
        Assert.NotSame(Options(provider, registrations[0]), Options(provider, registrations[1]));
    }

    [Fact]
    public void Register_TwoStorages_KeepTheirBasketsIsolated()
    {
        // Different baskets stay independent: each storage resolves its own options and client,
        // and the chain of a basket never reaches the other storage's client.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(bucket: "bucket-a", basket: "uploads"));
        services.AddSaS3FileStorage(Configure(bucket: "bucket-b", basket: "archive"));

        using var provider = services.BuildServiceProvider();

        var registrations = provider.GetServices<S3FileStorageRegistration>().ToArray();
        var storages = provider.GetServices<IFileStorage>().ToArray();

        Assert.Equal("uploads", storages[0].Basket);
        Assert.Equal("archive", storages[1].Basket);
        Assert.NotSame(Options(provider, registrations[0]), Options(provider, registrations[1]));
        Assert.NotSame(Client(provider, registrations[0]), Client(provider, registrations[1]));
    }

    [Fact]
    public void Register_TwoStoragesInOneBasket_CanProcessTheSameFileIdScheme()
    {
        // The premise of two-bucket failover: both storages answer CanProcess for the same fileId
        // scheme, so a read probes the first bucket and continues on a miss to the second.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(bucket: "first"));
        services.AddSaS3FileStorage(Configure(bucket: "second"));

        using var provider = services.BuildServiceProvider();

        var storages = provider.GetServices<IFileStorage>().ToArray();

        const string fileId = "s3://share/1/hello.txt";
        Assert.True(storages[0].CanProcess(fileId));
        Assert.True(storages[1].CanProcess(fileId));
    }

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddSaS3FileStorage(Configure());

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsTheValidatorOnce()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure());

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<S3FileStorageOptions>));
    }

    // ---------- что регистрируется ----------

    [Fact]
    public void Register_AddsExactlyOneFileStorage()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure());

        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
    }

    [Fact]
    public void Register_OneCallRegistersOneKeyedClientDescriptor()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure());
        services.AddSaS3FileStorage(Configure(bucket: "other"));

        int keyedClientDescriptors = services.Count(d =>
            d.ServiceType == typeof(IS3BucketClient) && d.IsKeyedService);

        Assert.Equal(2, keyedClientDescriptors);
    }

    [Fact]
    public void Register_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure());

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Register_RespectsAPreviouslyRegisteredTimeProvider()
    {
        var fake = new TimeProviderStub();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSaS3FileStorage(Configure());

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }

    // ---------- конвейер опций ----------

    [Fact]
    public void Register_NormalisesTheTrailingSlashOfTheEndpoint()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(endpoint: "http://localhost:9000/"));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("http://localhost:9000", Options(provider).Endpoint);
    }

    [Fact]
    public void Register_PostConfiguresBeforeValidating()
    {
        // Хвостовой слешш срезается до валидации: иначе валидатор видел бы сырое значение.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(endpoint: "  http://localhost:9000/  "));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal("http://localhost:9000", options.Endpoint);
        options.Validate();
    }

    [Fact]
    public void Register_CallbackPostConfigure_SeesTheNormalisedValue()
    {
        string? seen = null;

        var services = new ServiceCollection();
        services.AddSaS3FileStorage(o =>
        {
            Configure(endpoint: "http://localhost:9000/")(o);
            o.Options(ob => ob.PostConfigure(x => seen = x.Endpoint));
        });

        using var provider = services.BuildServiceProvider();

        // Материализует опции именно чтение .Value — сам IOptions<T> ленив.
        _ = Options(provider);

        Assert.Equal("http://localhost:9000", seen);
    }

    [Fact]
    public void Register_CallbackCanAddValidation()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(o => o
            .Options(ob => ob.Configure(x =>
            {
                x.AccessKey = "ROOTUSER";
                x.SecretKey = "ChangeMe123";
                x.Bucket = "mybucket";
                x.Endpoint = "http://localhost:9000";
                x.TotalRequestTimeout = TimeSpan.FromSeconds(30);
            })
            .Validate(x => x.TotalRequestTimeout > TimeSpan.FromMinutes(1), "Timeout is too aggressive.")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("too aggressive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_CallbackValidation_AddsToTheBuiltInChecks()
    {
        // Validate из callback не заменяет встроенную проверку: сообщаются обе ошибки.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(o => o
            .Options(ob => ob.Configure(x =>
            {
                x.AccessKey = "  ";
                x.SecretKey = "ChangeMe123";
                x.Bucket = "mybucket";
                x.Endpoint = "http://localhost:9000";
                x.TotalRequestTimeout = TimeSpan.FromSeconds(30);
            })
            .Validate(x => x.TotalRequestTimeout > TimeSpan.FromMinutes(1), "Timeout is too aggressive.")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("AccessKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("too aggressive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3FileStorage:Endpoint"] = "http://localhost:9000/",
                ["S3FileStorage:AccessKey"] = "ROOTUSER",
                ["S3FileStorage:SecretKey"] = "ChangeMe123",
                ["S3FileStorage:Bucket"] = "mybucket",
                ["S3FileStorage:Basket"] = "uploads",
                ["S3FileStorage:StorageType"] = "minio",
                ["S3FileStorage:IsReadOnly"] = "true",
                ["S3FileStorage:TotalRequestTimeout"] = "00:00:42",
                ["S3FileStorage:UseHttp2"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaS3FileStorage(b => b.FromConfiguration("S3FileStorage"));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal("http://localhost:9000", options.Endpoint);
        Assert.Equal("ROOTUSER", options.AccessKey);
        Assert.Equal("ChangeMe123", options.SecretKey);
        Assert.Equal("mybucket", options.Bucket);
        Assert.Equal("uploads", options.Basket);
        Assert.Equal("minio", options.StorageType);
        Assert.True(options.IsReadOnly);
        Assert.Equal(TimeSpan.FromSeconds(42), options.TotalRequestTimeout);
        Assert.True(options.UseHttp2);
    }

    [Fact]
    public void Register_ConfigureCallback_OverridesTheBoundSection()
    {
        // Options(...) воспроизводится после BindConfiguration, поэтому его Configure — последнее слово.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3FileStorage:Endpoint"] = "http://localhost:9000",
                ["S3FileStorage:AccessKey"] = "ROOTUSER",
                ["S3FileStorage:SecretKey"] = "ChangeMe123",
                ["S3FileStorage:Bucket"] = "mybucket",
                ["S3FileStorage:Basket"] = "uploads",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaS3FileStorage(o => o
            .FromConfiguration("S3FileStorage")
            .Options(ob => ob.Configure(x => x.Basket = "share")));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("share", Options(provider).Basket);
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration вызывается только когда задан путь, поэтому контейнер вовсе без
        // IConfiguration обязан работать. Значения по умолчанию здесь не проверяются напрямую: чтение
        // .Value прогоняет валидацию, а Endpoint без настройки закономерно пуст. Важно, что падает
        // именно валидация опций, а не поиск IConfiguration в контейнере.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("Endpoint", ex.Message, StringComparison.Ordinal);
    }

    // ---------- валидация ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(endpoint: "localhost:9000"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("absolute http or https URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_FailsValidation_ForAMalformedBasket()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(basket: "ab"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("Basket", ex.Message, StringComparison.Ordinal);
    }

    // ---------- что получает потребитель ----------

    [Fact]
    public void Register_ResolvesAStorageWithTheConfiguredNames()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(basket: "uploads", storageType: "minio"));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Equal("minio", storage.StorageType);
        Assert.Equal("uploads", storage.Basket);
        Assert.False(storage.IsReadOnly);
    }

    [Fact]
    public void Register_GivesTheBucketClient_TheSameNormalisedOptions()
    {
        // One set of values on both layers: S3FileStorageOptions itself inherits
        // S3BucketClientSetupOptions, so the keyed client of the registration is built from the
        // very instance its storage holds — there is no projection that could drop a property.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(endpoint: "http://localhost:9000/"));

        using var provider = services.BuildServiceProvider();

        var registration = provider.GetServices<S3FileStorageRegistration>().Single();
        var options = Options(provider, registration);
        var client = Client(provider, registration);

        Assert.Equal(options.Bucket, client.Bucket);
        Assert.Equal(options.Endpoint, client.Endpoint.ToString().TrimEnd('/'));
    }

    private sealed class TimeProviderStub : TimeProvider;
}
