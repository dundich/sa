using Microsoft.Extensions.Options;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Collects the configuration of one scalar settings type of a partitional module — the
/// configuration section (<see cref="FromConfiguration"/>) and the standard options pipeline
/// (<see cref="Configure"/> / <see cref="Options"/>) — in one registration delegate.
/// </summary>
/// <typeparam name="TSettings">
/// The settings type, e.g. <see cref="MigrationScheduleSettings"/>. Served as a named options
/// instance — one per module, so several keyed modules never see each other's values.
/// </typeparam>
/// <remarks>
/// Both configuring intents live here, so <see cref="IPartConfiguration.AddPartCache"/>,
/// <see cref="IPartConfiguration.AddPartMigrationSchedule"/> and
/// <see cref="IPartConfiguration.AddPartCleanupSchedule"/> take a single <c>configure</c> parameter.
/// The pipeline runs in a fixed order: the section binding (raw values) → the code
/// <see cref="Configure"/> delegates → the <see cref="Options"/> actions → validation. Configuration
/// is the baseline, code beats it, and <see cref="Options"/> is the escape hatch that has the final
/// word. Repeated calls merge (exactly like the previous delegate accumulation), and
/// <see cref="FromConfiguration"/> is last-wins.
/// </remarks>
public interface IPartSettingsBuilder<TSettings> where TSettings : class
{
    /// <summary>
    /// Binds <typeparamref name="TSettings"/> from the given configuration section, e.g.
    /// <c>"Partitional:Migration"</c> or <c>"Partitional:outbox:Migration"</c>.
    /// </summary>
    /// <remarks>
    /// Recorded on the builder and bound by the registration in the fixed slot before the code
    /// <see cref="Configure"/> delegates — so code always wins over configuration, no matter where
    /// this call sits in the callback. May be called several times; the last path wins. Requires an
    /// <c>IConfiguration</c> in DI (provided by any host).
    /// </remarks>
    /// <param name="sectionPath">Configuration section path.</param>
    IPartSettingsBuilder<TSettings> FromConfiguration(string sectionPath);

    /// <summary>
    /// Applies a code setting to the settings instance. Runs after the section binding, in
    /// registration order, so it beats configuration.
    /// </summary>
    /// <param name="configure">Callback receiving the service provider and the settings instance.</param>
    IPartSettingsBuilder<TSettings> Configure(Action<IServiceProvider, TSettings> configure);

    /// <summary>
    /// Hands the standard <see cref="OptionsBuilder{TOptions}"/> for <typeparamref name="TSettings"/>
    /// to the callback — the <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c> surface of the
    /// options pipeline.
    /// </summary>
    /// <remarks>
    /// Runs in the options pipeline <b>after</b> the section binding made for
    /// <see cref="FromConfiguration"/> and after the <see cref="Configure"/> delegates, so a
    /// <c>Configure</c> here has the final word over both, while a <c>Validate</c> adds to — rather
    /// than replaces — the built-in checks. May be called several times; the actions run in call order.
    /// </remarks>
    /// <param name="configureSettings">Callback receiving the settings options builder.</param>
    IPartSettingsBuilder<TSettings> Options(Action<OptionsBuilder<TSettings>> configureSettings);
}
