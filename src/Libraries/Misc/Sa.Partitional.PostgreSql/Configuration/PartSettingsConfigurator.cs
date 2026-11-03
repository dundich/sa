using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Sa.Partitional.PostgreSql.Configuration;

/// <summary>
/// Applies a module's accumulated <see cref="PartSettingsState{TSettings}"/> to the named options
/// instance: the configuration section first, then the code <c>Configure</c> delegates. The
/// <c>Options(...)</c> actions and validation are registered on the pipeline separately and run
/// after this configurator, which is what fixes the order at
/// <b>section → code → Options actions → validation</b> no matter how the registration calls were
/// split.
/// </summary>
internal sealed class PartSettingsConfigurator<TSettings>(
    PartSettingsState<TSettings> state,
    IServiceProvider services) : IConfigureNamedOptions<TSettings>
    where TSettings : class
{
    public void Configure(TSettings options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, TSettings options)
    {
        if (!string.Equals(name ?? Options.DefaultName, state.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (state.ConfigSectionPath is { } sectionPath)
        {
            IConfiguration configuration = services.GetService<IConfiguration>()
                ?? throw new InvalidOperationException(
                    $"Partitional settings for '{state.Name}' use FromConfiguration(\"{sectionPath}\"), " +
                    "but no IConfiguration is registered. Add one (a host does it automatically) or " +
                    "configure the settings in code.");

            state.Bind(options, configuration, sectionPath);
        }

        foreach (var codeAction in state.CodeActions)
        {
            codeAction(services, options);
        }
    }
}
