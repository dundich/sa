using Microsoft.Extensions.Options;

namespace Sa.Partitional.PostgreSql.Configuration;

/// <summary>
/// Default <see cref="IPartSettingsBuilder{TSettings}"/>: records the three configuring intents —
/// the configuration section, the code <c>Configure</c> delegates and the <c>Options(...)</c>
/// pipeline actions — so the registration can replay them in its fixed slot.
/// </summary>
internal sealed class PartSettingsBuilder<TSettings> : IPartSettingsBuilder<TSettings>
    where TSettings : class
{
    private readonly List<Action<IServiceProvider, TSettings>> _codeActions = [];
    private readonly List<Action<OptionsBuilder<TSettings>>> _optionsActions = [];

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by the registration before the code delegates.</summary>
    public string? ConfigSectionPath { get; private set; }

    /// <summary>Code delegates collected via <see cref="Configure"/>, in call order.</summary>
    public IReadOnlyList<Action<IServiceProvider, TSettings>> CodeActions => _codeActions;

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<TSettings>>> OptionsActions => _optionsActions;

    public IPartSettingsBuilder<TSettings> FromConfiguration(string sectionPath)
    {
        ArgumentNullException.ThrowIfNull(sectionPath);
        ConfigSectionPath = sectionPath;
        return this;
    }

    public IPartSettingsBuilder<TSettings> Configure(Action<IServiceProvider, TSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _codeActions.Add(configure);
        return this;
    }

    public IPartSettingsBuilder<TSettings> Options(Action<OptionsBuilder<TSettings>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _optionsActions.Add(configureSettings);
        return this;
    }
}
