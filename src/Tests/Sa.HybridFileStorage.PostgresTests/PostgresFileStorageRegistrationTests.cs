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
    private const string Key = "pg";

    // ---------- validation happens through the options pipeline, without touching the database ----------

    [Fact]
    public void Register_Rejects_QuotedTableName()
    {
        // Used to be silently Trim('"')-ed, and the storage then queried a *rewritten* name
        // ("my_files") that the DDL had never created.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "\"files\"")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.TableName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_TableNameWithSpaces()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "my files")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.TableName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_MissingTableName()
    {
        // TableName has no default any more: every registration must name the table it owns.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key);

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.TableName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_StorageTypeThatWouldBreakFileIds()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o =>
        {
            o.TableName = "files";
            o.StorageType = "pg:1";
        })));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.StorageType), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_CommaSeparatedSchemaName()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o =>
        {
            o.TableName = "files";
            o.SchemaName = "storage,public";
        })));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains(nameof(PostgresFileStorageOptions.SchemaName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Rejects_NonPositiveExpireDays()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o =>
        {
            o.TableName = "files";
            o.ExpireDays = 0;
        })));

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
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "\"files\"")));

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

        Assert.Throws<ArgumentNullException>(() => services.AddSaPostgreSqlFileStorage(Key));
        Assert.Throws<ArgumentNullException>(() => services.AddSaPostgreSqlFileStorageChained(Key));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaPostgreSqlFileStorage(Key, new PostgresFileStorageOptions()));
    }

    [Fact]
    public void Register_Rejects_NullOptions()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaPostgreSqlFileStorage(Key, (PostgresFileStorageOptions)null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_Rejects_BlankKey(string? key)
    {
        var services = new ServiceCollection();

        // null surfaces as the ArgumentNullException subtype, "" and whitespace as ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => services.AddSaPostgreSqlFileStorage(key!));
    }

    // ---------- duplicate registration ----------

    [Fact]
    public void Register_Throws_ForDifferentOptionsUnderTheSameKey()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "files")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "other"))));
        Assert.Contains("already registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_ForEqualOptionsUnderTheSameKeyToo()
    {
        // The guard is on the key, not on the values: two configuring calls for one key would stack
        // their Configure actions on one options instance anyway.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "files")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddSaPostgreSqlFileStorage(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "files"))));
        Assert.Contains("already registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Throws_WhenTheChainedOverloadIsMixedWithAnother()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key);

        Assert.Throws<InvalidOperationException>(() => services.AddSaPostgreSqlFileStorageChained(Key));
    }

    [Fact]
    public void Register_DifferentKeys_AreIndependent()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage("one", b => b.Options(ob => ob.Configure(o => o.TableName = "files_one")));
        services.AddSaPostgreSqlFileStorage("two", b => b.Options(ob => ob.Configure(o => o.TableName = "files_two")));

        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IFileStorage) && d.ServiceKey is null));
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(PostgresFileStorageRegistration)));
    }

    // ---------- explicit instance ----------

    [Fact]
    public void Register_DoesNotMutateTheSuppliedOptionsInstance()
    {
        var services = new ServiceCollection();
        var options = new PostgresFileStorageOptions { TableName = "files" };

        services.AddSaPostgreSqlFileStorage(Key, options);

        Assert.Equal("files", options.TableName);
    }

    [Fact]
    public void Register_AcceptsReadyMadeIOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage(Key, Options.Create(new PostgresFileStorageOptions { TableName = "files" }));

        using var provider = services.BuildServiceProvider();
        Assert.Contains(services, d => d.ServiceType == typeof(IFileStorage));
    }

    // ---------- schema resolution ----------

    [Fact]
    public void Schema_UsesTheExplicitNameFromOptions()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorageChained(Key, b => b.Options(ob => ob.Configure(o =>
        {
            o.TableName = "files";
            o.SchemaName = "explicit";
        })));

        using var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredKeyedService<PostgresFileStorageSchema>(Key);

        Assert.Equal("explicit", schema.Value);
    }

    [Fact]
    public void Schema_FallsBackToPublic_WhenNoDataSourceAndNoExplicitSchema()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorageChained(Key, b => b.Options(ob => ob.Configure(o => o.TableName = "files")));

        using var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredKeyedService<PostgresFileStorageSchema>(Key);

        Assert.Equal(PostgresFileStorageSchema.FallbackSchema, schema.Value);
    }
}
