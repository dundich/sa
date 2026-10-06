using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Data.PostgreSqlTests;

/// <summary>
/// Регистрация PostgreSQL data source: конвейер опций и поведение при повторных вызовах.
/// </summary>
/// <remarks>
/// Тесты не поднимают контейнер с базой: проверяется только конфигурация — она полностью отделена от
/// первого сетевого запроса. Интеграционные сценарии живут в <c>PgDataSourceTests</c>.
/// </remarks>
public sealed class SetupDataSourceRegistrationTests
{
    const string ValidConnectionString = "Host=127.0.0.1;Database=postgres;Username=u;Password=p";

    static PgDataSourceOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<PgDataSourceOptions>>().Value;

    // ---------- null / повторная регистрация ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaPostgreSqlDataSource());
    }

    [Fact]
    public void Register_RejectsASecondConfiguringCall()
    {
        // Второй вызов, несущий конфигурацию, добавит ещё один Configure-колбэк к тому же
        // безымянному PgDataSourceOptions, и оба Configure применились бы — настройки молча
        // склеились бы.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o.Configure(x => x.ConnectionString = ValidConnectionString));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlDataSource(o => o.Configure(x => x.ConnectionString = ValidConnectionString)));

        Assert.Contains("already been configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_AllowsRepeatedBareCalls()
    {
        // В отличие от файловых/S3-провайдеров, «голый» вызов может приходить несколько раз:
        // AddSaPartitional всегда регистрирует data source, а AddSaOutboxUsingPostgreSql — ещё и
        // для своих таблиц. Все три вызова — без configure и без секции.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource();
        services.AddSaPostgreSqlDataSource();
        services.AddSaPostgreSqlDataSource();

        // Дескрипторы конвейера не копируются: normalizer и validator — по одному.
        Assert.Single(services, d => d.ServiceType == typeof(IPostConfigureOptions<PgDataSourceOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<PgDataSourceOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IPgDataSource));
    }

    [Fact]
    public void Register_BareCallAfterAConfiguredCall_DoesNotThrow()
    {
        // Внутренний «голый» вызов (например, из AddSaPartitional) после пользовательского
        // конфигурирования обязан пройти мимо guard'а.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o.Configure(x => x.ConnectionString = ValidConnectionString));

        services.AddSaPostgreSqlDataSource();

        using var provider = services.BuildServiceProvider();
        Assert.Equal(ValidConnectionString, Options(provider).ConnectionString);
    }

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddSaPostgreSqlDataSource();

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsTheValidatorOnce()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o.Configure(x => x.ConnectionString = ValidConnectionString));

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<PgDataSourceOptions>));
    }

    // ---------- нормализация (PostConfigure) ----------

    [Fact]
    public void Register_NormalisesTheConnectionString()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o.Configure(x => x.ConnectionString = $"  {ValidConnectionString}  "));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(ValidConnectionString, Options(provider).ConnectionString);
    }

    // ---------- биндинг секции ----------

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Postgres:ConnectionString"] = ValidConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaPostgreSqlDataSource(configSectionPath: "Postgres");

        using var provider = services.BuildServiceProvider();

        Assert.Equal(ValidConnectionString, Options(provider).ConnectionString);
    }

    [Fact]
    public void Register_ConfigureCallback_OverridesTheBoundSection()
    {
        // Callback вызывается после BindConfiguration, поэтому его Configure — последнее слово.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Postgres:ConnectionString"] = ValidConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaPostgreSqlDataSource(
            configSectionPath: "Postgres",
            configure: o => o.Configure(x => x.ConnectionString = ValidConnectionString + ";Search Path=storage"));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(ValidConnectionString + ";Search Path=storage", Options(provider).ConnectionString);
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration вызывается только когда задан путь, поэтому контейнер вовсе без
        // IConfiguration обязан работать. Важно, что падает именно валидация опций, а не поиск
        // IConfiguration в контейнере.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("cannot be empty unless an NpgsqlDataSource is registered", ex.Message, StringComparison.Ordinal);
    }

    // ---------- валидация ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        // Неправильная строка всплывает как OptionsValidationException на первом resolve, а не
        // как ArgumentException изнутри Npgsql при первом соединении.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o.Configure(x => x.ConnectionString = "Host=127.0.0.1;Port=abc"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("not a valid connection string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_EmptyConnectionString_FailsWhenNoNpgsqlDataSourceIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IPgDataSource>());

        Assert.Contains("cannot be empty unless an NpgsqlDataSource is registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_EmptyConnectionString_FallsBackToRegisteredNpgsqlDataSource()
    {
        // Пустая строка — легитимный фолбэк: data source переиспользует уже зарегистрированный
        // NpgsqlDataSource (общий pool). Создание NpgsqlDataSource соединения не открывает,
        // поэтому тест остаётся без базы.
        var borrowed = NpgsqlDataSource.Create(ValidConnectionString + ";Search Path=storage");
        try
        {
            var services = new ServiceCollection();
            // Явный тип: Create возвращает Npgsql.PoolingDataSource, а фабрика ищет NpgsqlDataSource.
            services.AddSingleton<NpgsqlDataSource>(borrowed);
            services.AddSaPostgreSqlDataSource();

            using var provider = services.BuildServiceProvider();
            var dataSource = provider.GetRequiredService<IPgDataSource>();

            Assert.Equal("storage", dataSource.GetSearchPath());
        }
        finally
        {
            // Заёмный источник не принадлежит data source: PgDataSource намеренно не dispose
            // заёмный pool, поэтому диспозим его здесь.
            borrowed.Dispose();
        }
    }

    // ---------- callback с OptionsBuilder ----------

    [Fact]
    public void Register_CallbackCanAddAPostConfigure()
    {
        // PostConfigure из callback выполняется после нормализации этим методом.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o
            .Configure(x => x.ConnectionString = ValidConnectionString)
            .PostConfigure(x => x.ConnectionString += ";Search Path=storage"));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(ValidConnectionString + ";Search Path=storage", Options(provider).ConnectionString);
    }

    [Fact]
    public void Register_CallbackValidation_AddsToTheBuiltInChecks()
    {
        // Validate из callback не заменяет встроенную проверку: сообщаются обе ошибки.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o
            .Configure(x => x.ConnectionString = "Port=abc")
            .Validate(x => x.ConnectionString.Length > 200, "Connection string is too long."));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("not a valid connection string", ex.Message, StringComparison.Ordinal);
        Assert.Contains("too long", ex.Message, StringComparison.Ordinal);
    }
}
