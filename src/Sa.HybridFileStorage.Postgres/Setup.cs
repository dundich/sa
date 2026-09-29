using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IO;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Provides extension methods for registering the PostgreSQL file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configureOptions">An optional action to configure the PostgreSQL storage options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when the configured options are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the provider was already registered with different options.</exception>
    /// <remarks>
    /// The options are validated eagerly, at registration: an invalid table name, storage type or
    /// basket used to reach the DDL and only surfaced as <c>relation does not exist</c> on the
    /// first upload.
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PostgresFileStorageOptions();
        configureOptions?.Invoke(options);

        RegisterCore(services, options);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// Returns an <see cref="IPartConfiguration"/> for chaining DataSource configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configureOptions">An optional action to configure the PostgreSQL storage options.</param>
    /// <returns>An <see cref="IPartConfiguration"/> for chaining DataSource configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when the configured options are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the provider was already registered with different options.</exception>
    public static IPartConfiguration AddSaPostgreSqlFileStorageChained(
        this IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PostgresFileStorageOptions();
        configureOptions?.Invoke(options);

        return RegisterCore(services, options);
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Configuration options for the PostgreSQL storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when the supplied options are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the provider was already registered with different options.</exception>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        PostgresFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Copy so the caller's instance is never touched by the registration.
        RegisterCore(services, options.Copy());
        return services;
    }

    /// <summary>
    /// Performs the shared registration logic for all <see cref="AddSaPostgreSqlFileStorage"/> overloads.
    /// Registration is idempotent: a second call with equal options returns the existing partition
    /// configuration, while a call with different options throws <see cref="InvalidOperationException"/>
    /// instead of silently duplicating the partitioning setup, schedules, and <see cref="IFileStorage"/>
    /// (which would become ambiguous to resolve).
    /// </summary>
    private static IPartConfiguration RegisterCore(
        IServiceCollection services,
        PostgresFileStorageOptions options)
    {
        // Fail fast, before anything is registered: a caller must not be left with a half-wired
        // service collection when the options are rejected.
        options.Validate();

        // Validate() guarantees TableName/StorageType/Basket are already bare words and that
        // SchemaName is a single identifier, so this copy is the single source of truth for both
        // the DDL below and the storage. The marker snapshot is taken from it rather than from the
        // caller's instance, so a re-registration with equivalent options compares equal.
        var snapshot = options.Copy();

        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(PostgresFileStorageRegistration))
            ?.ImplementationInstance as PostgresFileStorageRegistration;
        if (existing is not null)
        {
            if (existing.Options == snapshot)
            {
                return existing.Configuration;
            }

            throw new InvalidOperationException(
                "AddSaPostgreSqlFileStorage has already been registered with different options. " +
                "Register the PostgreSQL file storage provider only once per service collection.");
        }

        // 1. RecyclableMemoryStreamManager (singleton, shared across all instances)
        services.TryAddSingleton<RecyclableMemoryStreamManager>();

        // 2. Resolve the schema once, so the DDL and the storage cannot disagree about it.
        PostgresFileStorageSchema.Register(services, snapshot.SchemaName);
        // 3. Partitioning setup + DataSource configuration
        IPartConfiguration partConfig = services.AddSaPartitional((sp, builder) =>
        {
            string schema = sp.GetRequiredService<PostgresFileStorageSchema>().Value;

            builder.AddSchema(schema, table =>
            {
                table.AddTable(snapshot.TableName, PostgresFileStorageTable.Columns)
                    .PartByList("tenant_id", "basket")
                    .PartByRange(snapshot.PgPartBy, PostgresFileStorageTable.PartByRangeFieldName);
            });
        });

        // 4. Schedule for creating new partitions
        partConfig.AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.ForwardDays = snapshot.MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(snapshot.ExpireDays);
        });

        // 5. Register IFileStorage singleton
        services.AddSingleton<IFileStorage>(sp => new PostgresFileStorage(
            dataSource: sp.GetRequiredService<IPgDataSource>(),
            partManager: sp.GetRequiredService<IPartitionManager>(),
            streamManager: sp.GetRequiredService<RecyclableMemoryStreamManager>(),
            options: snapshot with { SchemaName = sp.GetRequiredService<PostgresFileStorageSchema>().Value },
            timeProvider: sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton(new PostgresFileStorageRegistration(snapshot, partConfig));

        return partConfig;
    }
}

/// <summary>
/// Sentinel marker holding the registration state of the PostgreSQL file storage provider.
/// Ensures the partitioning setup, schedules, and <see cref="IFileStorage"/> are registered exactly once
/// and lets a conflicting second registration fail fast.
/// </summary>
internal sealed class PostgresFileStorageRegistration(
    PostgresFileStorageOptions options,
    IPartConfiguration configuration)
{
    /// <summary>
    /// Gets the validated options the provider was registered with. The schema is left as the
    /// caller configured it (<c>null</c> when auto-detected), so a re-registration that also
    /// relies on auto-detection compares equal.
    /// </summary>
    public PostgresFileStorageOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Gets the partition configuration produced by the registration.
    /// Returned by an idempotent re-registration with equal options.
    /// </summary>
    public IPartConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
}
