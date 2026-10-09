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
/// <c>AddSaPostgreSqlFileStorage</c> overload in <see cref="Setup"/> delegates here — the
/// fail-fast duplicate guard, the options pipeline, and the partitioning/schedule/DI wiring.
/// </summary>
internal static class PostgresFileStorageRegistrar
{
    /// <summary>
    /// Performs the shared registration logic for all <see cref="Setup.AddSaPostgreSqlFileStorage"/>
    /// overloads.
    /// Exactly one registration per service collection: the marker makes a second call fail fast —
    /// it would duplicate the partitioning setup, schedules and schema resolver (which
    /// <c>TryAdd</c>-based registrations would only half-ignore, leaving an ambiguous
    /// <see cref="IFileStorage"/>). Per-basket registrations arrive with the multi-basket stage,
    /// which replaces this guard.
    /// </summary>
    public static IPartConfiguration Register(
        IServiceCollection services,
        Action<IPostgresFileStorageBuilder>? configure,
        PostgresFileStorageOptions? instance)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(PostgresFileStorageRegistration)))
        {
            throw new InvalidOperationException(
                "AddSaPostgreSqlFileStorage has already been registered in this service collection. " +
                "A second call would duplicate the partitioning setup, schedules and schema resolver. " +
                "Register the PostgreSQL file storage provider only once per service collection.");
        }

        // The builder channel: invoke the callback first, so the section path it records can
        // shape the options-instance name below.
        PostgresFileStorageBuilder? builder = null;

        if (instance is null && configure is not null)
        {
            builder = new PostgresFileStorageBuilder();
            configure(builder);
        }

        // One registration, one named options instance — unique per registration, so two calls
        // never stack their Configure actions on one shared instance. The reader below resolves
        // it through IOptionsMonitor, which is also what makes a validation failure surface at
        // first read / host start rather than at registration.
        string optionsName = NextOptionsName(builder?.ConfigSectionPath);

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
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT.
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

        // 2. Resolve the schema once, so the DDL and the storage cannot disagree about it.
        //    A function now: the schema only materialises with the options instance.
        PostgresFileStorageSchema.Register(services, sp => readOptions(sp).SchemaName);

        // 3. Partitioning setup + DataSource configuration
        IPartConfiguration partConfig = services.AddSaPartitional((sp, partBuilder) =>
        {
            PostgresFileStorageOptions options = readOptions(sp);
            string schema = sp.GetRequiredService<PostgresFileStorageSchema>().Value;

            partBuilder.AddSchema(schema, table =>
            {
                table.AddTable(options.TableName, PostgresFileStorageTable.Columns)
                    .PartByList("tenant_id", "basket")
                    .PartByRange(options.PgPartBy, PostgresFileStorageTable.PartByRangeFieldName);
            });
        });

        // 4. Schedule for creating new partitions
        partConfig.AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.ForwardDays = readOptions(sp).MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(readOptions(sp).ExpireDays);
        });

        // 5. Register IFileStorage singleton. readOptions runs first, so an invalid options
        //    instance fails here — before IPgDataSource and the rest are even touched.
        services.AddSingleton<IFileStorage>(sp =>
        {
            PostgresFileStorageOptions options = readOptions(sp);

            return new PostgresFileStorage(
                dataSource: sp.GetRequiredService<IPgDataSource>(),
                partManager: sp.GetRequiredService<IPartitionManager>(),
                streamManager: sp.GetRequiredService<RecyclableMemoryStreamManager>(),
                options: options with { SchemaName = sp.GetRequiredService<PostgresFileStorageSchema>().Value },
                timeProvider: sp.GetRequiredService<TimeProvider>());
        });

        services.AddSingleton(new PostgresFileStorageRegistration(optionsName, partConfig));

        return partConfig;
    }

    /// <summary>
    /// Sequence for unique options-instance names within this assembly — one registration,
    /// one named instance (the name is an internal detail: tests read it back through the
    /// registration marker, never by hardcoding it).
    /// </summary>
    private static int s_optionsSequence;

    /// <summary>
    /// Names this registration's options instance: the section path when one was given
    /// (readable in diagnostics), the provider label otherwise, plus a sequence number that
    /// makes the name unique per registration.
    /// </summary>
    private static string NextOptionsName(string? sectionPath)
        => $"{sectionPath ?? "PostgresFileStorage"}#{Interlocked.Increment(ref s_optionsSequence)}";
}
