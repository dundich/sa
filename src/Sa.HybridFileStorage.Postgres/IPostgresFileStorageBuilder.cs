using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Collects the configuration of the PostgreSQL provider registered through
/// <see cref="Setup.AddSaPostgreSqlFileStorage"/>: the standard options pipeline
/// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) and the configuration
/// section, in one registration delegate.
/// </summary>
/// <remarks>
/// Both configuring intents live here — <see cref="FromConfiguration"/> for the section
/// and <see cref="Options"/> for the pipeline — so <c>AddSaPostgreSqlFileStorage</c> takes
/// a single <c>configure</c> parameter. The section binds in the fixed slot before the
/// <see cref="Options"/> actions, so a <c>Configure</c> there always wins over
/// configuration, wherever these calls sit in the callback.
/// <para>
/// The options materialise under a unique named instance, and validation runs against that
/// instance — at first read, or at host start via <c>ValidateOnStart()</c> — instead of
/// eagerly at registration.
/// </para>
/// </remarks>
public interface IPostgresFileStorageBuilder
{
    /// <summary>
    /// Binds <see cref="PostgresFileStorageOptions"/> from the given configuration section,
    /// e.g. <c>"PostgresFileStorage"</c>. The section may contain <c>SchemaName</c>,
    /// <c>TableName</c>, <c>StorageType</c>, <c>IsReadOnly</c>, <c>Basket</c>,
    /// <c>ExpireDays</c>, <c>MigrationScheduleForwardDays</c> and <c>PgPartBy</c>.
    /// </summary>
    /// <param name="configSectionPath">The configuration section path.</param>
    /// <returns>The same builder instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configSectionPath"/> is <c>null</c>.</exception>
    IPostgresFileStorageBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Adds actions to the standard options pipeline for this registration's named instance.
    /// </summary>
    /// <param name="configureSettings">
    /// The pipeline actions: <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>.
    /// A <c>Configure</c> here runs after the section binding and therefore beats it;
    /// a <c>Validate</c> adds to — rather than replaces — the built-in checks.
    /// </param>
    /// <returns>The same builder instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configureSettings"/> is <c>null</c>.</exception>
    IPostgresFileStorageBuilder Options(Action<OptionsBuilder<PostgresFileStorageOptions>> configureSettings);
}
