using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// The one place that knows how the PostgreSQL provider is registered: every public
/// <c>AddSaPostgreSqlFileStorage</c> overload in <see cref="Setup"/> delegates here — the per-key
/// duplicate guard, the options pipeline, the keyed partitional module (its own table, migration
/// and cleanup schedules) and the <see cref="IFileStorage"/> descriptors.
/// </summary>
/// <remarks>
/// Registration is <b>keyed</b>: every call registers one storage under its own key, with its own
/// named options instance, its own partitional module (and therefore its own retention) and its own
/// keyed <see cref="IFileStorage"/>. The same instance is also aliased as an unkeyed
/// <see cref="IFileStorage"/>, so the hybrid container's failover chain picks it up without extra
/// wiring — several registrations are tried in registration order and the first success ends the
/// operation.
/// </remarks>
internal static class PostgresFileStorageRegistrar
{
    /// <summary>
    /// Performs the shared registration logic for all <see cref="Setup.AddSaPostgreSqlFileStorage"/>
    /// overloads.
    /// </summary>
    public static IPartConfiguration Register(
        IServiceCollection services,
        string key,
        Action<IPostgresFileStorageBuilder>? configure,
        PostgresFileStorageOptions? instance)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // One registration per key: a second call would collide on the named options instance, the
        // keyed partitional module (its schedules and jobs) and the keyed IFileStorage descriptor.
        if (services.Any(d =>
                d.ServiceType == typeof(PostgresFileStorageRegistration) && Equals(d.ServiceKey, key)))
        {
            throw new InvalidOperationException(
                $"A PostgreSQL file storage with key \"{key}\" is already registered in this service " +
                "collection. Registration keys must be unique — pick another key for the second " +
                "instance.");
        }

        // The partitional module lives in its own key namespace, derived from the storage key, so a
        // file storage never collides with a user-registered partitional module that happens to use
        // the same key.
        string moduleKey = ModuleKey(key);

        // The builder channel: invoke the callback first, so the section path it records shapes the
        // options pipeline below.
        PostgresFileStorageBuilder? builder = null;

        if (instance is null && configure is not null)
        {
            builder = new PostgresFileStorageBuilder();
            configure(builder);
        }

        // One registration, one named options instance, named after the registration key (unique by
        // the guard above and readable in diagnostics).
        string optionsName = key;

        var optionsBuilder = services.AddOptions<PostgresFileStorageOptions>(optionsName);

        if (instance is not null)
        {
            // Explicit instance: copied at registration, replayed into the pipeline's fresh
            // instance as its only source — the caller's object stays untouched, and nothing
            // else can rewrite the values.
            PostgresFileStorageOptions captured = instance.Copy();
            optionsBuilder.Configure(target => captured.CopyTo(target));
        }
        else if (builder?.ConfigSectionPath is { } sectionPath)
        {
            // Fixed slot: the section binds after the callback has recorded it, before its
            // Options(...) actions replay — wherever those calls sit in the callback.
            optionsBuilder.BindConfiguration(sectionPath);
        }

        optionsBuilder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT. The registry is shared, so the
        // single validator sees every registration and rejects two storages owning one table.
        services.TryAddSingleton<PostgresFileStorageTableRegistry>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PostgresFileStorageOptions>, PostgresFileStorageOptionsValidator>());

        // Replayed in this slot — after the section binding and after this method's own
        // Validate — so the caller's Configure beats the section and its Validate runs after ours.
        if (builder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optionsBuilder);
            }
        }

        // The single reader every consumer below shares: IOptionsMonitor caches one instance
        // per name, so the DDL, the schedules, the schema resolver and the storage all see the
        // same options — and the first of them to run is what validates.
        Func<IServiceProvider, PostgresFileStorageOptions> readOptions =
            sp => sp.GetRequiredService<IOptionsMonitor<PostgresFileStorageOptions>>().Get(optionsName);

        // 1. RecyclableMemoryStreamManager (singleton, shared across all instances)
        services.TryAddSingleton<RecyclableMemoryStreamManager>();

        // 2. TimeProvider (singleton, shared)
        services.TryAddSingleton(TimeProvider.System);

        // 3. Resolve the schema once per registration, so the DDL and the storage cannot disagree.
        PostgresFileStorageSchema.Register(services, key, moduleKey, sp => readOptions(sp).SchemaName);

        // 4. The keyed partitional module: one table declaration, one migration schedule and one
        //    cleanup schedule — the storage's own retention. The data source is the module's named
        //    one when the chain configures it (AddDataSource), the shared default one otherwise.
        IPartConfiguration partConfig = services.AddSaPartitional(moduleKey, (sp, partBuilder) =>
        {
            PostgresFileStorageOptions options = readOptions(sp);
            string schema = sp.GetRequiredKeyedService<PostgresFileStorageSchema>(key).Value;

            partBuilder.AddSchema(schema, schemaBuilder =>
            {
                schemaBuilder.AddTable(options.TableName, PostgresFileStorageTable.Columns)
                    .PartByList("tenant_id", "basket")
                    .PartByRange(PgPartBy.FromRange(options.PartByRange), PostgresFileStorageTable.PartByRangeFieldName);
            });
        });

        // Migration: pre-create this storage's upcoming partitions.
        partConfig.AddPartMigrationSchedule(b => b.Configure((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.ForwardDays = readOptions(sp).MigrationScheduleForwardDays;
        }))
        // Cleanup: drop this storage's old partitions after its own retention.
        .AddPartCleanupSchedule(b => b.Configure((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(readOptions(sp).ExpireDays);
        }));

        // 5. Register the keyed IFileStorage — resolvable by key — and alias it as an unkeyed
        //    IFileStorage, which is what the hybrid container collects via
        //    sp.GetServices<IFileStorage>() and orders into the failover chain.
        services.AddKeyedSingleton<IFileStorage>(key, CreateStorage);
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredKeyedService<IFileStorage>(key));

        services.AddKeyedSingleton(key, new PostgresFileStorageRegistration(key, moduleKey, optionsName, partConfig));

        return partConfig;

        IFileStorage CreateStorage(IServiceProvider sp, object? _)
        {
            // readOptions runs first, so an invalid options instance fails here — before
            // IPgDataSource and the rest are even touched.
            PostgresFileStorageOptions options = readOptions(sp);

            // The module's own named source when the chain pinned one, the shared default source
            // next, and an application-registered NpgsqlDataSource last — so a container that only
            // called AddNpgsqlDataSource(...) needs no separate AddDataSource(...) here.
            IPgDataSource dataSource = PgDataSourceResolver.Resolve(sp, moduleKey);

            return new PostgresFileStorage(
                dataSource: dataSource,
                partManager: sp.GetRequiredKeyedService<IPartitionManager>(moduleKey),
                streamManager: sp.GetRequiredService<RecyclableMemoryStreamManager>(),
                options: options with { SchemaName = sp.GetRequiredKeyedService<PostgresFileStorageSchema>(key).Value },
                timeProvider: sp.GetRequiredService<TimeProvider>());
        }
    }

    /// <summary>
    /// The partitional module key of a file-storage registration: the namespaced, derived identity
    /// that keeps the storage's own module apart from any user-registered module.
    /// </summary>
    public static string ModuleKey(string key)
        => $"{nameof(PostgresFileStorage)}:{key}";
}
