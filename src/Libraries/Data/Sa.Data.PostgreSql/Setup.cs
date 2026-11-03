using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sa.Data.PostgreSql;

public static class Setup
{
    /// <summary>
    /// Registers the (unnamed) PostgreSQL data source on the standard options pipeline.
    /// This is the original, backward-compatible registration; see
    /// <see cref="AddSaPostgreSqlDataSource(string, Action{IDataSourceBuilder}?)"/> for the keyed form.
    /// </summary>
    public static IServiceCollection AddSaPostgreSqlDataSource(this IServiceCollection services,
        Action<IDataSourceBuilder>? configure = null)
        => AddSaPostgreSqlDataSource(services, string.Empty, configure);

    /// <summary>
    /// Registers a <b>keyed</b> PostgreSQL data source: one <see cref="IPgDataSource"/> per
    /// <paramref name="key"/>. This is how an application talks to several PostgreSQL databases
    /// from one container — <c>services.AddSaPostgreSqlDataSource("db1", ...)</c> and
    /// <c>services.AddSaPostgreSqlDataSource("db2", ...)</c> are independent data sources, each with
    /// its own options and connection string, resolved by
    /// <c>sp.GetRequiredKeyedService&lt;IPgDataSource&gt;("db1")</c>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="key">
    /// The key of the data source. Must be unique per registration. The empty string
    /// <c>string.Empty</c> is the unnamed, backward-compatible data source — the same overload is
    /// what the original one-parameter form delegates to, so an empty key is always allowed.
    /// </param>
    /// <param name="configure">
    /// The configuration channel: the section via <see cref="IDataSourceBuilder.FromConfiguration"/>
    /// and the standard pipeline (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) via
    /// <see cref="IDataSourceBuilder.Options"/>, in the same delegate. Invoked once, immediately;
    /// its <c>Options(...)</c> actions are replayed after this method's own registrations, so their
    /// <c>Configure</c> runs last and their <c>Validate</c> adds to — rather than replaces — the
    /// built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a second *configuring* call (one carrying <paramref name="configure"/>) for the
    /// same <paramref name="key"/> is made in this collection. A call for a <b>different</b> key is
    /// always allowed.
    /// </exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: the section binding
    /// (<see cref="IDataSourceBuilder.FromConfiguration"/>, raw values) and then the caller's
    /// <see cref="IDataSourceBuilder.Options"/> <c>Configure</c> calls →
    /// <see cref="PgDataSourceNormalizer"/> (normalisation) and then the caller's
    /// <see cref="IDataSourceBuilder.Options"/> <c>PostConfigure</c> calls →
    /// <see cref="PgDataSourceOptionsValidator"/>. <c>ValidateOnStart()</c> turns an invalid
    /// configuration into an <c>OptionsValidationException</c> at host start, and the same check
    /// fires when the (keyed) options are first read, so a bare <c>ServiceCollection</c> fails fast
    /// on the first data-source resolve rather than deep in a query.
    /// <para>
    /// <b>Repeated calls.</b> A key that is already *configured* in this collection rejects a second
    /// configuring call for the same key (it would stack two <c>Configure</c> callbacks onto the same
    /// <see cref="PgDataSourceOptions"/> instance and silently merge them). A call for a different key
    /// is independent and is always allowed — that is the whole point of keyed registration.
    /// A bare call (no <paramref name="configure"/>) is repeatable for any key and is a no-op on the
    /// options pipeline.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlDataSource(
        this IServiceCollection services,
        string key,
        Action<IDataSourceBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(key);

        // Both configuring intents (the section and the Options actions) live inside
        // `configure`, so `configure is not null` alone answers the guard — a bare call
        // cannot carry either.
        var willConfigure = configure is not null;

        // The guard is per key: it fires only when a configuring call for THIS key already
        // exists in the collection. A different key registers independently, which is what makes
        // several data sources coexist in one container.
        if (willConfigure && IsKeyConfigured(services, key))
        {
            throw new InvalidOperationException(
                "AddSaPostgreSqlDataSource has already been configured for key " +
                (key.Length == 0 ? "(the default)" : $"\"{key}\"") +
                " in this service collection. A second configuring call for the same key would stack " +
                "another Configure callback onto the same PgDataSourceOptions instance, so the settings " +
                "would silently merge. Configure the same PostgreSQL data source key only once per " +
                "service collection — use a different key for a different database.");
        }

        var optionsBuilder = services.AddOptions<PgDataSourceOptions>(key);

        DataSourceBuilder? builder = null;

        if (configure is not null)
        {
            builder = new DataSourceBuilder();
            configure(builder);
        }

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        var sectionPath = builder?.ConfigSectionPath;

        if (sectionPath is not null)
        {
            optionsBuilder.BindConfiguration(sectionPath);
        }

        // Registered by type via TryAddEnumerable so a repeated call (bare internal calls are
        // legitimate — AddSaPartitional and AddSaOutboxUsingPostgreSql each register a data
        // source) deduplicates instead of stacking. The pipeline runs these for every named
        // instance, so a single registration serves every key. Runs in the PostConfigure phase,
        // i.e. after every Configure callback, so validation always sees the normalised value.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<PgDataSourceOptions>, PgDataSourceNormalizer>());

        optionsBuilder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT. The validator is resolved by DI,
        // which injects its IServiceProvider, so it can tell "empty connection string + a
        // NpgsqlDataSource is registered" (a legal fallback) apart from a genuinely broken setup.
        // A single registration runs for every named instance, so it covers every key.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PgDataSourceOptions>, PgDataSourceOptionsValidator>());

        // Replayed in this slot — after the section binding — so an Options(...).Configure
        // has the last word over configuration, and its Validate runs after this method's
        // own checks.
        if (builder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optionsBuilder);
            }
        }

        if (willConfigure)
        {
            // A per-key sentinel, registered keyed under THIS key: the guard above matches on
            // ServiceKey, so each configured key records its own marker and a different key can
            // still be configured in the same collection. A plain (unkeyed) TryAddSingleton would
            // dedupe on the marker type alone and keep only the first key's marker.
            services.TryAddKeyedSingleton(key, new PgDataSourceConfigurationMarker());
        }

        // The data source is registered keyed, so one container can hold many of them. For the
        // default key the bare (unkeyed) IPgDataSource is also registered as an alias, so code
        // that resolves the unnamed service keeps working when the default data source is what
        // is wanted.
        RegisterDataSource(services, key);

        return services;
    }

    // Registers the data source for the given key. The factory is always keyed; for the default
    // key a second, unkeyed alias is added so existing consumers (and the test fixtures that
    // resolve the unnamed service) keep working. The alias resolves the keyed ("") instance, so
    // both names share one data source (and one connection pool) instead of building a second.
    // Both are Try-Add, so a repeated bare call for the same key (AddSaPartitional and
    // AddSaOutboxUsingPostgreSql each register one) dedupes instead of stacking a second factory.
    private static void RegisterDataSource(IServiceCollection services, string key)
    {
        services.TryAddKeyedSingleton<IPgDataSource>(key, (sp, _) => CreateDataSource(sp, key));

        if (key.Length == 0)
        {
            services.TryAddSingleton<IPgDataSource>(sp => sp.GetRequiredKeyedService<IPgDataSource>(key));
        }
    }

    private static bool IsKeyConfigured(IServiceCollection services, string key)
        => services.Any(d => d.ServiceType == typeof(PgDataSourceConfigurationMarker)
            && Equals(d.ServiceKey, key));

    // Assembles the data source lazily at first resolve for the given key. The connection string is
    // read from the (keyed) options pipeline, whose read runs the validators, so a misconfiguration
    // surfaces as an OptionsValidationException here, not as a bare FormatException from inside
    // Npgsql.
    // An empty connection string falls back to an NpgsqlDataSource registered with the SAME key
    // (sharing its connection pool); for the default key that is the unkeyed NpgsqlDataSource.
    private static IPgDataSource CreateDataSource(IServiceProvider sp, string key)
    {
        var options = sp.GetRequiredService<IOptionsMonitor<PgDataSourceOptions>>().Get(key);

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PgDataSource(options.ConnectionString);
        }

        var borrowed = key.Length == 0
            ? sp.GetService<NpgsqlDataSource>()
            : sp.GetKeyedService<NpgsqlDataSource>(key);

        if (borrowed is null)
        {
            throw new InvalidOperationException(
                "PgDataSourceOptions.ConnectionString is" +
                (key.Length == 0 ? "" : $" for key \"{key}\"") +
                " empty and no NpgsqlDataSource is registered" +
                (key.Length == 0 ? "" : $" with key \"{key}\"") +
                " in the service collection. Provide a connection string or register an NpgsqlDataSource.");
        }

        return new PgDataSource(borrowed);
    }
}

/// <summary>
/// Normalises <see cref="PgDataSourceOptions"/> in the PostConfigure phase of the options pipeline
/// (before validation). Registered as a type rather than a lambda so that repeated registration
/// deduplicates: a bare data source registration may legitimately happen more than once per
/// container (<c>AddSaPartitional</c> and <c>AddSaOutboxUsingPostgreSql</c> both do so by design).
/// The pipeline runs it for every named instance, so one registration covers every key.
/// </summary>
internal sealed class PgDataSourceNormalizer : IPostConfigureOptions<PgDataSourceOptions>
{
    public void PostConfigure(string? name, PgDataSourceOptions options)
        => options.Normalize();
}

/// <summary>
/// Sentinel recording that <see cref="Setup.AddSaPostgreSqlDataSource"/> was configured (a
/// <c>configure</c> callback — section via <see cref="IDataSourceBuilder.FromConfiguration"/> or
/// settings via <see cref="IDataSourceBuilder.Options"/>) in this collection <b>for a given key</b>,
/// so a second configuring call for the same key fails fast instead of silently stacking two
/// <c>Configure</c> callbacks onto the same <see cref="PgDataSourceOptions"/> instance. The key is
/// carried as the marker's <see cref="ServiceDescriptor.ServiceKey"/>; a marker for a different key
/// does not block that key — it is a different data source.
/// </summary>
internal sealed class PgDataSourceConfigurationMarker
{
}
