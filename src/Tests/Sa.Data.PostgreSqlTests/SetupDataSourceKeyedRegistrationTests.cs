using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Data.PostgreSqlTests;

/// <summary>
/// Keyed-регистрация PostgreSQL data source: одна БД на ключ, независимые опции и data source,
/// per-key guard на повторную configuring-регистрацию.
/// </summary>
/// <remarks>
/// Контейнер с базой не поднимается: проверяется только конфигурация — создание
/// <see cref="NpgsqlDataSource"/> соединения не открывает. Интеграционные сценарии живут в
/// <c>PgDataSourceTests</c>. <see cref="PgDataSourceConfigurationMarker"/> намеренно не
/// упоминается: он <c>internal</c>, а у этой сборки нет доступа к внутренностям
/// <c>Sa.Data.PostgreSql</c>; per-key guard проверяется через публичный сценарий.
/// </remarks>
public sealed class SetupDataSourceKeyedRegistrationTests
{
    const string ValidConnectionString = "Host=127.0.0.1;Database=postgres;Username=u;Password=p";

    static string WithSearchPath(string searchPath) => $"{ValidConnectionString};Search Path={searchPath}";

    // ---------- несколько БД в одном контейнере ----------

    [Fact]
    public void Register_TwoKeys_ResolveIndependentDataSources()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource("db1", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("one"))));
        services.AddSaPostgreSqlDataSource("db2", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("two"))));

        using var provider = services.BuildServiceProvider();

        var db1 = provider.GetRequiredKeyedService<IPgDataSource>("db1");
        var db2 = provider.GetRequiredKeyedService<IPgDataSource>("db2");

        Assert.NotSame(db1, db2);
        Assert.Equal("one", db1.GetSearchPath());
        Assert.Equal("two", db2.GetSearchPath());
    }

    [Fact]
    public void Register_TwoKeys_HaveIndependentOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource("db1", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("one"))));
        services.AddSaPostgreSqlDataSource("db2", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("two"))));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PgDataSourceOptions>>();

        Assert.Equal(WithSearchPath("one"), monitor.Get("db1").ConnectionString);
        Assert.Equal(WithSearchPath("two"), monitor.Get("db2").ConnectionString);
    }

    // ---------- per-key guard ----------

    [Fact]
    public void Register_SecondConfiguringCallForTheSameKey_Throws()
    {
        // Второй configuring-вызов для db2 должен быть отвергнут даже после того, как db1 уже
        // сконфигурирован: guard обязан быть per-key, а не «один на коллекцию». Иначе его маркер
        // остался бы только у первого ключа, и настройки db2 молча склеились бы.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource("db1", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("one"))));
        services.AddSaPostgreSqlDataSource("db2", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("two"))));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlDataSource("db2", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("two")))));

        Assert.Contains("\"db2\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_BareCallForAConfiguredKey_DoesNotThrow()
    {
        // «Голый» вызов (внутренние AddSaPartitional / AddSaOutboxUsingPostgreSql) для уже
        // сконфигурированного ключа обязан пройти мимо guard'а.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource("db1", o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("one"))));

        services.AddSaPostgreSqlDataSource("db1");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(WithSearchPath("one"), provider.GetRequiredService<IOptionsMonitor<PgDataSourceOptions>>().Get("db1").ConnectionString);
    }

    // ---------- дефолтный ключ: alias на keyed-инстанс ----------

    [Fact]
    public void Register_DefaultKeyAlias_ResolvesTheSameInstanceAsTheKeyedEmptyString()
    {
        // Безымянный IPgDataSource и keyed ("") — один и тот же инстанс, а не два pool'а.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("one"))));

        using var provider = services.BuildServiceProvider();

        var unkeyed = provider.GetRequiredService<IPgDataSource>();
        var keyed = provider.GetRequiredKeyedService<IPgDataSource>(string.Empty);

        Assert.Same(unkeyed, keyed);
    }

    // ---------- keyed-фолбэк на NpgsqlDataSource ----------

    [Fact]
    public void Register_KeyedEmptyConnectionString_FallsBackToKeyedNpgsqlDataSource()
    {
        var borrowed = NpgsqlDataSource.Create(WithSearchPath("storage"));
        try
        {
            var services = new ServiceCollection();
            // Явный тип: Create возвращает Npgsql.PoolingDataSource, а фабрика ищет NpgsqlDataSource.
            services.AddKeyedSingleton<NpgsqlDataSource>("db1", borrowed);
            services.AddSaPostgreSqlDataSource("db1");

            using var provider = services.BuildServiceProvider();
            var dataSource = provider.GetRequiredKeyedService<IPgDataSource>("db1");

            Assert.Equal("storage", dataSource.GetSearchPath());
        }
        finally
        {
            // Заёмный источник не принадлежит data source: PgDataSource намеренно не dispose
            // заёмный pool, поэтому диспозим его здесь.
            borrowed.Dispose();
        }
    }

    [Fact]
    public void Register_KeyedEmptyConnectionString_WithoutKeyedNpgsqlDataSource_Fails()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource("db1");

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredKeyedService<IPgDataSource>("db1"));

        Assert.Contains("key \"db1\"", ex.Message, StringComparison.Ordinal);
    }
}
