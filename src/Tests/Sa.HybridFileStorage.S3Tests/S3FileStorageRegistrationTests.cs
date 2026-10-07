using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// Registration-level behaviour of the S3 provider: the options pipeline the caller configures, and
/// what the single registration ends up putting in the container.
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

    static S3FileStorageOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<S3FileStorageOptions>>().Value;

    // ---------- null / повторная регистрация ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaS3FileStorage());
    }

    [Fact]
    public void Register_RejectsASecondCall()
    {
        // Безымянный S3FileStorageOptions принадлежит регистрации: второй вызов добавил бы ещё один
        // IConfigureOptions в тот же экземпляр, и обе Configure-функции применились бы — настройки
        // молча слились бы, а IFileStorage появился бы в двух экземплярах.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure());

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSaS3FileStorage(Configure()));

        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<S3FileStorageOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IFileStorage));
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
    public void Register_AddsExactlyOneBucketClient()
    {
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure());
        int after = services.Count;

        // The AddHttpClient pipeline must be registered exactly once: first-wins would otherwise
        // depend on registration order rather than on the guard.
        Assert.Throws<InvalidOperationException>(() => services.AddSaS3FileStorage(Configure()));

        Assert.Equal(after, services.Count);
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
        // Один набор значений на оба слоя: S3FileStorageOptions сам наследует
        // S3BucketClientSetupOptions, поэтому проекции, которая могла бы что-то потерять, нет.
        var services = new ServiceCollection();
        services.AddSaS3FileStorage(Configure(endpoint: "http://localhost:9000/"));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);
        var clientSettings = provider.GetRequiredService<S3BucketSettings>();

        Assert.Same(options, clientSettings);
        Assert.Equal("http://localhost:9000", clientSettings.Endpoint);
    }

    private sealed class TimeProviderStub : TimeProvider;
}
