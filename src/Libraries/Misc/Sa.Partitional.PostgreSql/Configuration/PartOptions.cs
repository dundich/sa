using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Partitional.PostgreSql.Configuration;

/// <summary>
/// Shared wiring of a scalar settings type onto the standard <c>Microsoft.Extensions.Options</c>
/// pipeline, used by the cache, migration and cleanup registrations.
/// </summary>
/// <remarks>
/// The three configuring intents (the configuration section, the code <c>Configure</c> delegates and
/// the <c>Options(...)</c> actions) are collected into one per-module <see cref="PartSettingsState{TSettings}"/>
/// and applied by a single <see cref="PartSettingsConfigurator{TSettings}"/> registered the first time
/// a module touches the settings type. That makes the pipeline order
/// <b>section → code → Options actions → validation</b> independent of how many
/// <c>AddPartCache</c>/<c>AddPartMigrationSchedule</c>/<c>AddPartCleanupSchedule</c> calls a module
/// makes and in what order — so, for example, the explicit <c>AsBackgroundJob</c> argument passed to
/// <c>AddSaPartitional</c> always beats an <c>AsBackgroundJob</c> key found in the section, even
/// though the framework records it before the caller chains its <c>FromConfiguration</c> call.
/// </remarks>
internal static class PartOptions
{
    /// <summary>
    /// Named-options name of a module: the module key, or the default (unnamed) instance for the
    /// keyless module. Mirrors the per-module isolation the keyed singleton settings had before.
    /// </summary>
    public static string Name(string? key) => key ?? Options.DefaultName;

    /// <summary>
    /// Registers <typeparamref name="TSettings"/> as a named options instance and records the
    /// intents of one registration call. The configuration binder is the concrete
    /// <paramref name="bindConfiguration"/> callback supplied by the module setup, so the section
    /// binding stays on a concretely typed call site and keeps the source-generated (Native AOT)
    /// binder.
    /// </summary>
    public static OptionsBuilder<TSettings> Configure<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TSettings>(
        IServiceCollection services,
        string name,
        Action<TSettings, IConfiguration, string> bindConfiguration,
        Action<IPartSettingsBuilder<TSettings>>? configure)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bindConfiguration);

        OptionsBuilder<TSettings> options = services.AddOptions<TSettings>(name);

        PartSettingsState<TSettings> state = GetOrAddState(services, name, bindConfiguration);

        if (configure is not null)
        {
            var builder = new PartSettingsBuilder<TSettings>();
            configure(builder);

            // Last FromConfiguration wins; the section is applied before the code delegates below.
            if (builder.ConfigSectionPath is { } sectionPath)
            {
                state.ConfigSectionPath = sectionPath;
            }

            state.CodeActions.AddRange(builder.CodeActions);

            // Replayed now: these land after the configurator registered above, so an
            // Options(...).Configure has the final word over both configuration and code.
            foreach (var optionsAction in builder.OptionsActions)
            {
                optionsAction(options);
            }
        }

        return options;
    }

    private static PartSettingsState<TSettings> GetOrAddState<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TSettings>(
        IServiceCollection services,
        string name,
        Action<TSettings, IConfiguration, string> bindConfiguration)
        where TSettings : class
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(PartSettingsState<TSettings>)
                && descriptor.ImplementationInstance is PartSettingsState<TSettings> existing
                && string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        var state = new PartSettingsState<TSettings>(name, bindConfiguration);
        services.AddSingleton(state);
        services.AddSingleton<IConfigureOptions<TSettings>>(
            sp => new PartSettingsConfigurator<TSettings>(state, sp));

        // Validation at host start, once per named instance; the same check also runs when the
        // options are first read.
        services.AddOptions<TSettings>(name).ValidateOnStart();

        return state;
    }
}
