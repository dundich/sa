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
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions = null)
    {
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
    public static IPartConfiguration AddSaPostgreSqlFileStorageChained(
        this IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions = null)
    {
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
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        PostgresFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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
        // Snapshot the options before anything runs: the schema is auto-detected lazily
        // (mutating the instance) when the settings builder resolves, so the stored state
        // must reflect the user's original configuration for idempotency comparison.
        var snapshot = options.Copy();

        // Idempotency guard: compare against the snapshot of the previous registration.
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(RegistrationMarker))
            ?.ImplementationInstance as RegistrationMarker;
        if (existing is not null)
        {
            if (existing.Options == options)
            {
                return existing.Configuration;
            }

            throw new InvalidOperationException(
                "AddSaPostgreSqlFileStorage has already been registered with different options. " +
                "Register the PostgreSQL file storage provider only once per service collection.");
        }

        // Trim quotes from table name if accidentally included
        options.TableName = options.TableName.Trim('"');

        // 1. RecyclableMemoryStreamManager (singleton, shared across all instances)
        services.TryAddSingleton<RecyclableMemoryStreamManager>();

        // 2. Partitioning setup + DataSource configuration
        IPartConfiguration partConfig = services.AddSaPartitional((sp, builder) =>
        {
            // Auto-detect schema from the connection search_path only when the user did not set one explicitly.
            // The first schema of a comma-separated search path is the effective one for table resolution.
            if (options.SchemaName is null)
            {
                var searchPath = sp.GetRequiredService<IPgDataSource>().GetSearchPath();
                options.SchemaName = string.IsNullOrWhiteSpace(searchPath)
                    ? "public"
                    : searchPath.Split(',')[0].Trim();
            }

            builder.AddSchema(options.SchemaName, schema =>
            {
                schema.AddTable(options.TableName,
                    "id TEXT NOT NULL",
                    "name TEXT NOT NULL",
                    "size INT NOT NULL",
                    "file_ext TEXT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "basket TEXT NOT NULL",
                    "data BYTEA NOT NULL"
                )
                .PartByList("tenant_id", "basket")
                .PartByRange(options.PgPartBy, "created_at");
            });
        });

        // 3. Schedule for creating new partitions
        partConfig.AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.ForwardDays = options.MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(options.ExpireDays);
        });

        // 4. Register IFileStorage singleton
        services.AddSingleton<IFileStorage>(sp => new PostgresFileStorage(
            dataSource: sp.GetRequiredService<IPgDataSource>(),
            partManager: sp.GetRequiredService<IPartitionManager>(),
            streamManager: sp.GetRequiredService<RecyclableMemoryStreamManager>(),
            options: options,
            timeProvider: sp.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddSingleton(new RegistrationMarker(snapshot, partConfig));

        return partConfig;
    }
}

/// <summary>
/// Sentinel marker holding the registration state of the PostgreSQL file storage provider.
/// Ensures the partitioning setup, schedules, and <see cref="IFileStorage"/> are registered exactly once
/// and lets a conflicting second registration fail fast.
/// </summary>
internal sealed class RegistrationMarker(
    PostgresFileStorageOptions options,
    IPartConfiguration configuration)
{
    /// <summary>
    /// Gets a snapshot of the options the provider was registered with (taken before any
    /// resolution-time mutation, so it reflects the user's original configuration).
    /// </summary>
    public PostgresFileStorageOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Gets the partition configuration produced by the registration.
    /// Returned by an idempotent re-registration with equal options.
    /// </summary>
    public IPartConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
}
