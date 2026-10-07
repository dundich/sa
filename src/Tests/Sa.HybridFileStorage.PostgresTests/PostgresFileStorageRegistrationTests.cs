using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// Registration-level behaviour: what fails at startup and what fails only at the first upload.
/// The options were previously unvalidated, so every one of these used to be a runtime surprise.
/// </summary>
public sealed class PostgresFileStorageRegistrationTests
{
    // ---------- validation happens through the options pipeline, without touching the database ----------

    [Fact]
    public void Register_Rejects_QuotedTableName()
    {
        // Used to be silently Trim('"')-ed, and the storage then queried a *rewritten* name
        // ("my_files") that the DDL had never created.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "\"files\"")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.TableName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_TableNameWithSpaces()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "my files")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.TableName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_StorageTypeThatWouldBreakFileIds()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.StorageType = "pg:1")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.StorageType), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_CommaSeparatedSchemaName()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.SchemaName = "storage,public")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.SchemaName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_NonPositiveExpireDays()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.ExpireDays = 0)));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.ExpireDays), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_KeepsValidationForThePipeline_FailsAtFirstRead_NotAtRegistration()
    {
        // Under the options pipeline the failure moves to the first read (or host start with
        // ValidateOnStart) — registration itself must succeed, like for fs/s3.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "\"files\"")));

        // Registration did not throw — the collection is fully wired.
        Assert.Contains(services, d => d.ServiceType == typeof(IFileStorage));

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
    }

    // ---------- null / argument checking ----------

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

    // ---------- duplicate registration ----------

    [Fact]
    public void Register_Throws_ForDifferentOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "files")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "other"))));
        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_ForEqualOptionsToo()
    {
        // The value-comparison idempotency is gone with the options pipeline: under a lazy
        // section the options are not materialisable at registration time, and two configuring
        // calls would stack their Configure actions on one instance anyway. The marker makes
        // any second call fail fast; the multi-basket stage replaces this guard.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "files")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlFileStorage(b => b.Options(ob => ob.Configure(o => o.TableName = "files"))));
        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_WhenTheChainedOverloadIsMixedWithAnother()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage();

        Assert.Throws<InvalidOperationException>(() => services.AddSaPostgreSqlFileStorageChained());
    }

    // ---------- explicit instance ----------

    [Fact]
    public void Register_DoesNotMutateTheSuppliedOptionsInstance()
    {
        var services = new ServiceCollection();
        var options = new PostgresFileStorageOptions { TableName = "files" };

        services.AddSaPostgreSqlFileStorage(options);

        Assert.Equal("files", options.TableName);
    }

    [Fact]
    public void Register_AcceptsReadyMadeIOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Options.Create(new PostgresFileStorageOptions { TableName = "files" }));

        using var provider = services.BuildServiceProvider();
        Assert.Contains(services, d => d.ServiceType == typeof(IFileStorage));
    }

    // ---------- schema resolution ----------

    [Fact]
    public void Schema_UsesTheExplicitNameFromOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorageChained(b => b.Options(ob => ob.Configure(o => o.SchemaName = "explicit")));

        using var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredService<PostgresFileStorageSchema>();

        Assert.Equal("explicit", schema.Value);
    }

    [Fact]
    public void Schema_FallsBackToPublic_WhenNoDataSourceAndNoExplicitSchema()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorageChained();

        using var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredService<PostgresFileStorageSchema>();

        Assert.Equal(PostgresFileStorageSchema.FallbackSchema, schema.Value);
    }
}
