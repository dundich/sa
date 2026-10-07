using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
    /// Registers the PostgreSQL file storage provider using the standard options pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">
    /// The configuration channel: the section via <see cref="IPostgresFileStorageBuilder.FromConfiguration"/>
    /// and the standard pipeline (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) via
    /// <see cref="IPostgresFileStorageBuilder.Options"/>, in the same delegate. Invoked once, immediately;
    /// its <c>Options(...)</c> actions are replayed after this method's own registrations, so their
    /// <c>Configure</c> runs last and their <c>Validate</c> adds to — rather than replaces — the
    /// built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: the section binding (<c>FromConfiguration</c>,
    /// raw values) and then the caller's <c>Options(...)</c> <c>Configure</c> calls → validation.
    /// Validation itself is the standard pipeline's: it sees the normalised result and fires at
    /// the first read of the options (the storage, the DDL or the schema resolver materialise
    /// them), or at host start with <c>ValidateOnStart()</c> — so an invalid table name or
    /// storage type fails as an <see cref="OptionsValidationException"/> naming the property
    /// instead of reaching the DDL and surfacing as <c>relation does not exist</c> on the first
    /// upload. In a test host without a started host the first read is what fires it.
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<IPostgresFileStorageBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        RegisterCore(services, configure, instance: null);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider using the standard options pipeline.
    /// Returns an <see cref="IPartConfiguration"/> for chaining DataSource configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">The configuration channel: section and pipeline actions in one delegate.</param>
    /// <returns>An <see cref="IPartConfiguration"/> for chaining DataSource configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    public static IPartConfiguration AddSaPostgreSqlFileStorageChained(
        this IServiceCollection services,
        Action<IPostgresFileStorageBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return RegisterCore(services, configure, instance: null);
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider from an explicit options instance.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Configuration options for the PostgreSQL storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    /// <remarks>
    /// The instance is copied — the caller's object is never the storage's and never touched —
    /// and the copy feeds this registration's named pipeline instance as its only source, so
    /// validation runs here exactly as for the builder channel: at first read or host start,
    /// not at registration.
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        PostgresFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterCore(services, configure: null, options);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider from a ready-made options instance,
    /// typically one the caller resolved from its own pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">The ready-made options; <see cref="IOptions{T}.Value"/> is read once, here.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        IOptions<PostgresFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterCore(services, configure: null, options.Value);
        return services;
    }

    /// <summary>
    /// Performs the shared registration logic for all <see cref="AddSaPostgreSqlFileStorage"/> overloads.
    /// Exactly one registration per service collection: the marker makes a second call fail fast —
    /// it would duplicate the partitioning setup, schedules and schema resolver (which
    /// <c>TryAdd</c>-based registrations would only half-ignore, leaving an ambiguous
    /// <see cref="IFileStorage"/>). Per-basket registrations arrive with the multi-basket stage,
    /// which replaces this guard.
    /// </summary>
    private static IPartConfiguration RegisterCore(
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

/// <summary>
/// Sentinel marker holding the registration state of the PostgreSQL file storage provider.
/// Ensures the partitioning setup, schedules, and <see cref="IFileStorage"/> are registered exactly once
/// and lets a conflicting second registration fail fast. Carries the registration's
/// options-instance name — the handle tests resolve the named instance by.
/// </summary>
internal sealed class PostgresFileStorageRegistration(
    string optionsName,
    IPartConfiguration configuration)
{
    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } = optionsName ?? throw new ArgumentNullException(nameof(optionsName));

    /// <summary>
    /// Gets the partition configuration produced by the registration.
    /// </summary>
    public IPartConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
}
