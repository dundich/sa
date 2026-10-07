using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Sa.Data.S3;

namespace Sa.Data.S3Tests;

/// <summary>
/// Регистрация именованного клиента S3: конвейер опций и то, что из него доходит до HTTP-обвязки.
/// </summary>
/// <remarks>
/// Тесты не поднимают контейнер: проверяется только конфигурация — она полностью отделена от
/// первого сетевого запроса. Интеграционные сценарии живут в <c>SetupBucketClientShould</c>.
/// </remarks>
public sealed class SetupBucketClientTests
{
    /// <summary>Имя клиента в тестах: все регистрации идут под ним, пока не проверяется multi-name.</summary>
    private const string ClientName = "test-client";

    private static Action<IS3BucketClientBuilder> Configure(
        string bucket = "mybucket",
        string endpoint = "http://localhost:9000",
        string accessKey = "ROOTUSER",
        string secretKey = "ChangeMe123")
        => b => b.Options(o => o.Configure(x =>
        {
            x.AccessKey = accessKey;
            x.SecretKey = secretKey;
            x.Bucket = bucket;
            x.Endpoint = endpoint;
        }));

    // Опции читаются по имени клиента — ровно тому имени, под которым зарегистрирован клиент.
    static S3BucketClientSetupOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptionsMonitor<S3BucketClientSetupOptions>>().Get(ClientName);

    // ---------- null / пустое имя / повторная регистрация ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaS3BucketClient(ClientName));
    }

    [Fact]
    public void Register_RejectsAnEmptyName()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddSaS3BucketClient("   ", Configure()));
    }

    [Fact]
    public void Register_RejectsASecondCallUnderTheSameName()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSaS3BucketClient(ClientName, Configure()));

        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<S3BucketClientSetupOptions>));
    }

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddSaS3BucketClient(ClientName, Configure());

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsTheValidatorOnce()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<S3BucketClientSetupOptions>));
    }

    [Fact]
    public void Register_AddsExactlyOneBucketClientPerName()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());
        int after = services.Count;

        Assert.Throws<InvalidOperationException>(() => services.AddSaS3BucketClient(ClientName, Configure()));

        Assert.Equal(after, services.Count);
    }

    [Fact]
    public void Register_RegistersTheSettingsAsTheClientSettings()
    {
        // S3BucketClientSetupOptions сам наследует S3BucketSettings, поэтому клиент получает ровно
        // тот же объект — копировать поля вручную не нужно, а значит и забыть поле невозможно.
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());

        using var provider = services.BuildServiceProvider();

        Assert.Same(Options(provider), provider.GetRequiredKeyedService<S3BucketSettings>(ClientName));
    }

    // ---------- конвейер опций ----------

    [Fact]
    public void Register_NormalisesTheEndpoint()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure(endpoint: "  HTTP://Localhost:9000/  "));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("http://localhost:9000", Options(provider).Endpoint);
    }

    [Fact]
    public void Register_CallbackCanAddAPostConfigure()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, o => o
            .Options(ob => ob.Configure(x =>
            {
                x.AccessKey = "ROOTUSER";
                x.SecretKey = "ChangeMe123";
                x.Bucket = "mybucket";
                x.Endpoint = "http://localhost:9000";
                x.Region = "us-east-1";
            })
            .PostConfigure(x => x.Region = "eu-central-1")));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("eu-central-1", Options(provider).Region);
    }

    [Fact]
    public void Register_CallbackValidation_AddsToTheBuiltInChecks()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, o => o
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
        Assert.Contains("Timeout is too aggressive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3:Endpoint"] = "http://localhost:9000/",
                ["S3:AccessKey"] = "ROOTUSER",
                ["S3:SecretKey"] = "ChangeMe123",
                ["S3:Bucket"] = "mybucket",
                ["S3:Region"] = "us-west-2",
                ["S3:UseHttp2"] = "true",
                ["S3:TotalRequestTimeout"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaS3BucketClient(ClientName, b => b.FromConfiguration("S3"));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal("http://localhost:9000", options.Endpoint);
        Assert.Equal("ROOTUSER", options.AccessKey);
        Assert.Equal("ChangeMe123", options.SecretKey);
        Assert.Equal("mybucket", options.Bucket);
        Assert.Equal("us-west-2", options.Region);
        Assert.True(options.UseHttp2);
        Assert.Equal(TimeSpan.FromMinutes(1), options.TotalRequestTimeout);
    }

    [Fact]
    public void Register_ConfigureCallback_OverridesTheBoundSection()
    {
        // Options(...) воспроизводится после BindConfiguration, поэтому его Configure — последнее слово.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3:Endpoint"] = "http://localhost:9000",
                ["S3:AccessKey"] = "ROOTUSER",
                ["S3:SecretKey"] = "ChangeMe123",
                ["S3:Bucket"] = "mybucket",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaS3BucketClient(ClientName, o => o
            .FromConfiguration("S3")
            .Options(ob => ob.Configure(x => x.Bucket = "other")));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("other", Options(provider).Bucket);
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration вызывается только когда задан путь, поэтому контейнер вовсе без
        // IConfiguration обязан работать. Важно, что падает именно валидация опций, а не поиск
        // IConfiguration в контейнере.
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName);

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("Endpoint", ex.Message, StringComparison.Ordinal);
    }

    // ---------- валидация ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        // Раньше клиент вообще ничем не проверялся, и неверный адрес всплывал как UriFormatException
        // изнутри new Uri(...) при первом обращении.
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure(endpoint: "localhost:9000"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("absolute http or https URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_IsUsableOutsideDi()
    {
        var options = new S3BucketClientSetupOptions();

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => options.Validate());
    }

    // ---------- HTTP-обвязка ----------

    [Fact]
    public void Register_SetsTheHandlerLifetime_ForTheNamedClientOnly()
    {
        // SetHandlerLifetime принимает только TimeSpan, а значение ленивое, поэтому lifetime пишется
        // прямо в HttpClientFactoryOptions под именем клиента — иначе он затёр бы время жизни
        // handler'ов у всех остальных типизированных клиентов приложения.
        var lifetime = TimeSpan.FromHours(2);

        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, o => o
            .Options(ob => ob.Configure(x =>
            {
                x.AccessKey = "ROOTUSER";
                x.SecretKey = "ChangeMe123";
                x.Bucket = "mybucket";
                x.Endpoint = "http://localhost:9000";
                x.HandlerLifetime = lifetime;
            })));
        services.AddHttpClient("another").SetHandlerLifetime(TimeSpan.FromMinutes(7));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        Assert.Equal(lifetime, monitor.Get(ClientName).HandlerLifetime);
        Assert.Equal(TimeSpan.FromMinutes(7), monitor.Get("another").HandlerLifetime);
    }

    [Fact]
    public void Register_SetsTheResilienceTotalRequestTimeout()
    {
        // Дедлайн задаёт стратегия Polly TotalRequestTimeout: AddStandardResilienceHandler
        // принудительно ставит HttpClient.Timeout в InfiniteTimeSpan, поэтому значение из опций
        // обязано дойти именно до неё.
        var timeout = TimeSpan.FromSeconds(42);

        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, o => o
            .Options(ob => ob.Configure(x =>
            {
                x.AccessKey = "ROOTUSER";
                x.SecretKey = "ChangeMe123";
                x.Bucket = "mybucket";
                x.Endpoint = "http://localhost:9000";
                x.TotalRequestTimeout = timeout;
            })));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>();

        // The pipeline name is derived from the client name by AddStandardResilienceHandler; probe
        // both spellings so a mismatch shows up as a value diff rather than a silent pass.
        Assert.NotNull(Setup.ResiliencePipelineName);

        var actual = monitor.Get(Setup.ResiliencePipelineName).TotalRequestTimeout.Timeout;

        Assert.Equal(timeout, actual);
    }

    [Fact]
    public void Register_HandsTheClientABaseAddress()
    {
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        Assert.Equal(new Uri("http://localhost:9000"), client.BaseAddress);
    }

    [Fact]
    public void Register_LeavesTheTimeoutToTheResiliencePipeline()
    {
        // AddStandardResilienceHandler принудительно ставит HttpClient.Timeout в
        // Timeout.InfiniteTimeSpan: дедлайн должен задавать Polly, иначе клиент отвалился бы по
        // таймауту HttpClient раньше, чем отработают ретраи.
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    // ---------- несколько именованных клиентов ----------

    [Fact]
    public void Register_Named_TwoNames_TwoIndependentClients()
    {
        // Публичный именованный путь: два вызова с разными именами дают двух полностью
        // независимых клиентов — свой keyed-клиент, свои keyed-настройки, свой HttpClient.
        var services = new ServiceCollection();
        services.AddSaS3BucketClient("orders", Configure(bucket: "orders-bucket"));
        services.AddSaS3BucketClient("archive", Configure(bucket: "archive-bucket"));

        using var provider = services.BuildServiceProvider();

        var orders = (S3BucketClient)provider.GetRequiredKeyedService<IS3BucketClient>("orders");
        var archive = (S3BucketClient)provider.GetRequiredKeyedService<IS3BucketClient>("archive");

        Assert.Equal("orders-bucket", orders.Bucket);
        Assert.Equal("archive-bucket", archive.Bucket);
        Assert.NotSame(orders, archive);
        Assert.NotSame(
            provider.GetRequiredKeyedService<S3BucketSettings>("orders"),
            provider.GetRequiredKeyedService<S3BucketSettings>("archive"));

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        Assert.Equal("http://localhost:9000", factory.CreateClient("orders").BaseAddress?.ToString().TrimEnd('/'));
        Assert.Equal("http://localhost:9000", factory.CreateClient("archive").BaseAddress?.ToString().TrimEnd('/'));
    }

    [Fact]
    public void Register_Named_RegistersNoUnkeyedAliases()
    {
        // У именованной регистрации нет unkeyed-псевдонимов: unkeyed-резолв обязан отдавать null,
        // а не «какого-то» клиента из списка.
        var services = new ServiceCollection();
        services.AddSaS3BucketClient(ClientName, Configure());

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IS3BucketClient>());
        Assert.Null(provider.GetService<S3BucketSettings>());
    }

    [Fact]
    public void Core_RegistersTwoClients_EachWithItsOwnKeyedClientAndSettings()
    {
        // The multi-instance capability of the HTTP wiring: two calls with different names yield
        // two independent clients — own named HttpClient, own keyed settings, own keyed client.
        var services = new ServiceCollection();

        services.AddSaS3BucketClientCore("first", _ => new S3BucketClientSetupOptions
        {
            AccessKey = "ROOTUSER",
            SecretKey = "ChangeMe123",
            Bucket = "bucket-a",
            Endpoint = "http://localhost:9000",
        });
        services.AddSaS3BucketClientCore("second", _ => new S3BucketClientSetupOptions
        {
            AccessKey = "ROOTUSER",
            SecretKey = "ChangeMe123",
            Bucket = "bucket-b",
            Endpoint = "http://localhost:9000",
        });

        using var provider = services.BuildServiceProvider();

        var first = (S3BucketClient)provider.GetRequiredKeyedService<IS3BucketClient>("first");
        var second = (S3BucketClient)provider.GetRequiredKeyedService<IS3BucketClient>("second");

        Assert.Equal("bucket-a", first.Bucket);
        Assert.Equal("bucket-b", second.Bucket);
        Assert.NotSame(first, second);
        Assert.NotSame(
            provider.GetRequiredKeyedService<S3BucketSettings>("first"),
            provider.GetRequiredKeyedService<S3BucketSettings>("second"));

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        Assert.Equal("http://localhost:9000", factory.CreateClient("first").BaseAddress?.ToString().TrimEnd('/'));
        Assert.Equal("http://localhost:9000", factory.CreateClient("second").BaseAddress?.ToString().TrimEnd('/'));
    }
}
