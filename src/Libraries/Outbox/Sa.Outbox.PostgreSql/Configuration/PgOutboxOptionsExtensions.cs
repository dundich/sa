using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Configuration-section extensions for the PostgreSQL Outbox: bind the scalar root settings
/// (<see cref="PgOutboxOptions"/>) so schema/table names and the migration/cleanup schedules can be
/// changed by deployment configuration rather than by code.
/// </summary>
public static class PgOutboxOptionsExtensions
{
    /// <summary>
    /// Binds <see cref="PgOutboxOptions"/> from an <c>IConfiguration</c> section and applies the
    /// non-null values to <see cref="PgOutboxSettings"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs inside the <c>PgOutboxSettings</c> factory, after the schema inherited from the
    /// connection string's <c>Search Path</c> and after any earlier <c>WithOutboxSettings</c> action,
    /// and before the settings are validated. A missing <c>IConfiguration</c> or a missing section is
    /// a no-op.
    /// </para>
    /// <para>
    /// Only the scalar surface is bound (schema, table names, fill factors, migration/cleanup).
    /// Column definitions stay code-only: they are raw SQL fragments spliced into DDL verbatim.
    /// </para>
    /// </remarks>
    /// <param name="configuration">The PostgreSQL Outbox configuration.</param>
    /// <param name="sectionPath">The section path, for example <c>Sa:Outbox:Pg</c>.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sectionPath"/> is null, empty or whitespace.</exception>
    public static IPgOutboxConfiguration WithOutboxSettingsFromConfiguration(
        this IPgOutboxConfiguration configuration,
        string sectionPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        return configuration.WithOutboxSettings((sp, settings) =>
        {
            IConfiguration? config = sp.GetService<IConfiguration>();
            if (config is null) return;

            IConfigurationSection section = config.GetSection(sectionPath);
            if (!section.Exists()) return;

            PgOutboxOptions options = new();
            section.Bind(options);
            options.ApplyTo(settings);
        });
    }
}
