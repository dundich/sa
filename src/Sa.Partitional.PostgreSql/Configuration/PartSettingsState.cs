using Microsoft.Extensions.Configuration;

namespace Sa.Partitional.PostgreSql.Configuration;

/// <summary>
/// Per-module accumulator of the scalar-settings intents. One instance per
/// (<typeparamref name="TSettings"/>, options name), created on the first
/// <c>AddPartCache</c>/<c>AddPartMigrationSchedule</c>/<c>AddPartCleanupSchedule</c> call and mutated
/// by every later call, so the whole module's configuration ends up in one place regardless of how
/// the calls are split across the fluent chain.
/// </summary>
/// <remarks>
/// Kept in the service collection as an implementation instance (see <c>PartOptions.GetOrAddState</c>),
/// so later calls find and extend it instead of registering a second options pipeline.
/// </remarks>
internal sealed class PartSettingsState<TSettings>(string name, Action<TSettings, IConfiguration, string> bind)
    where TSettings : class
{
    /// <summary>Options name of the module — its key, or the default (unnamed) instance.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// Section recorded via <c>FromConfiguration</c>. Last call wins; <c>null</c> means the module
    /// uses the class defaults and only the code settings.
    /// </summary>
    public string? ConfigSectionPath { get; set; }

    /// <summary>
    /// Concrete binder for this settings type, supplied by the module setup. It keeps the binding
    /// call on a concretely typed call site, which is what lets the configuration source generator
    /// bake the binder under Native AOT.
    /// </summary>
    public Action<TSettings, IConfiguration, string> Bind { get; } = bind;

    /// <summary>Code delegates collected via <c>Configure</c>, in call order, applied after the section.</summary>
    public List<Action<IServiceProvider, TSettings>> CodeActions { get; } = [];
}
