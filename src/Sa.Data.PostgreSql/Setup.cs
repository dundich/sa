using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sa.Data.PostgreSql;

public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL data source on the standard options pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">
    /// An optional callback receiving the <see cref="OptionsBuilder{TOptions}"/> for this data source, so
    /// configuration goes through the standard <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>
    /// methods rather than a bespoke overload.
    /// </param>
    /// <param name="configSectionPath">
    /// An optional configuration section to bind the options from, e.g. <c>"Postgres"</c>.
    /// Bound first, so a <c>Configure</c> call in <paramref name="configure"/> has the last word.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a second *configuring* call (one carrying <paramref name="configure"/> or
    /// <paramref name="configSectionPath"/>) is made in this collection.
    /// </exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: <c>Configure</c> (raw values) →
    /// <c>PostConfigure</c> (normalisation) → <c>PostConfigure</c> calls made in
    /// <paramref name="configure"/> → validation. <c>ValidateOnStart()</c> turns an invalid
    /// configuration into an <see cref="OptionsValidationException"/> at host start, and the same
    /// check also fires when <c>IOptions&lt;PgDataSourceOptions&gt;.Value</c> is first read, so a bare
    /// <c>ServiceCollection</c> fails fast on the first data-source resolve rather than deep in a query.
    /// <para>
    /// The callback is invoked after this method's own registrations, so its <c>Configure</c> runs last
    /// and its <c>Validate</c> adds to — rather than replaces — the built-in checks.
    /// </para>
    /// <para>
    /// <b>Repeated calls.</b> Unlike the filesystem/S3 providers, this method is legitimately called
    /// more than once per container: <c>AddSaPartitional</c> always registers a data source
    /// (<c>AddDataSource()</c>), and <c>AddSaOutboxUsingPostgreSql</c> does so again for its outbox
    /// tables. Those internal calls carry no <paramref name="configure"/> and no
    /// <paramref name="configSectionPath"/>, so they are a no-op on the options pipeline and are
    /// deliberately not rejected. The guard fires only on a second call that *carries configuration*,
    /// because that is the one shape that would stack two <c>Configure</c> callbacks onto the same
    /// unnamed <see cref="PgDataSourceOptions"/> instance and silently merge them.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlDataSource(
        this IServiceCollection services,
        Action<OptionsBuilder<PgDataSourceOptions>>? configure = null,
        string? configSectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var willConfigure = configure is not null || configSectionPath is not null;

        if (willConfigure
            && services.Any(d => d.ServiceType == typeof(PgDataSourceConfigurationMarker)))
        {
            throw new InvalidOperationException(
                "AddSaPostgreSqlDataSource has already been configured in this service collection. " +
                "A second configuring call would stack another Configure callback onto the same " +
                "PgDataSourceOptions instance, so the settings would silently merge. " +
                "Configure the PostgreSQL data source only once per service collection.");
        }

        var builder = services.AddOptions<PgDataSourceOptions>();

        if (configSectionPath is not null)
        {
            builder.BindConfiguration(configSectionPath);
        }

        // Registered by type via TryAddEnumerable so a repeated call (bare internal calls are
        // legitimate — AddSaPartitional and AddSaOutboxUsingPostgreSql each register a data
        // source) deduplicates instead of stacking. Runs in the PostConfigure phase, i.e. after
        // every Configure callback, so validation always sees the normalised value.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<PgDataSourceOptions>, PgDataSourceNormalizer>());

        builder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT. The validator is resolved by DI,
        // which injects its IServiceProvider, so it can tell "empty connection string + a
        // NpgsqlDataSource is registered" (a legal fallback) apart from a genuinely broken setup.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PgDataSourceOptions>, PgDataSourceOptionsValidator>());

        // Invoked last, so the caller's Configure runs after any section binding and its Validate
        // runs after this method's own checks.
        configure?.Invoke(builder);

        if (willConfigure)
        {
            services.TryAddSingleton(new PgDataSourceConfigurationMarker());
        }

        services.TryAddSingleton<IPgDataSource>(CreateDataSource);

        return services;
    }

    // Assembles the data source lazily at first resolve. The connection string is read from the
    // options pipeline (whose .Value runs the validators), so a misconfiguration surfaces as an
    // OptionsValidationException here, not as a bare FormatException from inside Npgsql.
    // An empty connection string falls back to an already-registered NpgsqlDataSource, sharing its
    // connection pool.
    private static IPgDataSource CreateDataSource(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<PgDataSourceOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PgDataSource(options.ConnectionString);
        }

        var borrowed = sp.GetService<NpgsqlDataSource>()
            ?? throw new InvalidOperationException(
                "PgDataSourceOptions.ConnectionString is empty and no NpgsqlDataSource is registered " +
                "in the service collection. Provide a connection string or register an NpgsqlDataSource.");

        return new PgDataSource(borrowed);
    }
}

/// <summary>
/// Normalises <see cref="PgDataSourceOptions"/> in the PostConfigure phase of the options pipeline
/// (before validation). Registered as a type rather than a lambda so that repeated registration
/// deduplicates: a bare data source registration may legitimately happen more than once per
/// container (<c>AddSaPartitional</c> and <c>AddSaOutboxUsingPostgreSql</c> both do so by design).
/// </summary>
internal sealed class PgDataSourceNormalizer : IPostConfigureOptions<PgDataSourceOptions>
{
    public void PostConfigure(string? name, PgDataSourceOptions options)
        => options.Normalize();
}

/// <summary>
/// Sentinel marker recording that <see cref="Setup.AddSaPostgreSqlDataSource"/> was configured (with a
/// <c>configure</c> callback or a configuration section) in this collection, so a second *configuring*
/// call fails fast instead of silently stacking two <c>Configure</c> callbacks onto the same options
/// instance. Bare internal calls (no configure / no section) neither check nor create it.
/// </summary>
internal sealed class PgDataSourceConfigurationMarker
{
}
