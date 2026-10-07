using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// Registration-level behaviour: what fails at startup and what fails only at the first upload.
/// The options were previously unvalidated, so every one of these used to be a runtime surprise.
/// </summary>
public sealed class PostgresFileStorageRegistrationTests
{
    // ---------- validation happens at registration, without touching the database ----------

    [Fact]
    public void Register_Rejects_QuotedTableName()
    {
        // Used to be silently Trim('"')-ed, and the storage then queried a *rewritten* name
        // ("my_files") that the DDL had never created.
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.TableName = "\"files\""));
        Assert.Equal(nameof(PostgresFileStorageOptions.TableName), ex.ParamName);
    }

    [Fact]
    public void Register_Rejects_TableNameWithSpaces()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.TableName = "my files"));
    }

    [Fact]
    public void Register_Rejects_StorageTypeThatWouldBreakFileIds()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.StorageType = "pg:1"));
        Assert.Equal(nameof(PostgresFileStorageOptions.StorageType), ex.ParamName);
    }

    [Fact]
    public void Register_Rejects_CommaSeparatedSchemaName()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.SchemaName = "storage,public"));
        Assert.Equal(nameof(PostgresFileStorageOptions.SchemaName), ex.ParamName);
    }

    [Fact]
    public void Register_Rejects_NonPositiveExpireDays()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.ExpireDays = 0));
    }

    [Fact]
    public void Register_LeavesTheServiceCollectionUntouched_WhenValidationFails()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.TableName = "\"files\""));

        Assert.Empty(services);
    }

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaPostgreSqlFileStorage());
        Assert.Throws<ArgumentNullException>(() => services.AddSaPostgreSqlFileStorageChained());
        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaPostgreSqlFileStorage(new PostgresFileStorageOptions()));
    }

    [Fact]
    public void Register_Rejects_NullOptions()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaPostgreSqlFileStorage((PostgresFileStorageOptions)null!));
    }

    // ---------- idempotency guard ----------

    [Fact]
    public void Register_IsIdempotent_ForEqualOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(o => o.TableName = "files");
        int after = services.Count;

        services.AddSaPostgreSqlFileStorage(o => o.TableName = "files");

        Assert.Equal(after, services.Count);
    }

    [Fact]
    public void Register_Throws_ForDifferentOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(o => o.TableName = "files");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlFileStorage(o => o.TableName = "other"));
        Assert.Contains("already been registered with different options", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_WhenTheChainedOverloadIsMixedWithAnother()
    {
        // The guard keys on the marker, so the two entry points cannot be used to register twice.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(o => o.TableName = "files");

        Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlFileStorageChained(o => o.TableName = "other"));
    }

    [Fact]
    public void Register_DoesNotMutateTheSuppliedOptionsInstance()
    {
        var options = new PostgresFileStorageOptions { TableName = "files" };
        var services = new ServiceCollection();

        services.AddSaPostgreSqlFileStorage(options);

        Assert.Equal("files", options.TableName);
        Assert.Null(options.SchemaName);
    }

    // ---------- schema resolution ----------

    [Theory]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=u;Password=p", "public")]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path=storage", "storage")]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path=storage,public", "storage")]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path= storage , audit", "storage")]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path=,", "public")]
    public void Schema_FallsBackToTheFirstSearchPathEntry(
        string connectionString, string expected)
    {
        // No database round trip: GetSearchPath parses the connection string.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorageChained()
            .AddDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = connectionString)));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetRequiredService<PostgresFileStorageSchema>().Value);
    }

    [Fact]
    public void Schema_PrefersTheExplicitName()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorageChained(o => o.SchemaName = "explicit")
            .AddDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString =
                "Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path=from_search_path")));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("explicit", provider.GetRequiredService<PostgresFileStorageSchema>().Value);
    }

    [Fact]
    public void Schema_FallsBackToPublic_WhenNoDataSourceIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage();

        using var provider = services.BuildServiceProvider();

        Assert.Equal("public", provider.GetRequiredService<PostgresFileStorageSchema>().Value);
    }
}
