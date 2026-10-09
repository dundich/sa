using Microsoft.Extensions.Options;

namespace Sa.Data.TempFolder;

/// <summary>
/// Default <see cref="ITempFolderBuilder"/>: records the configuring intents — the configuration
/// section, the <c>Options(...)</c> pipeline actions and the strategy overrides — so
/// <see cref="Setup.AddSaTempFolder"/> can replay them in its fixed slot.
/// </summary>
internal sealed class TempFolderBuilder : ITempFolderBuilder
{
    /// <summary>Lowest-precedence defaults collected via <see cref="Defaults"/>; replayed by Setup before section binding.</summary>
    private readonly List<Action<TempFolderOptions>> _defaultsActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<TempFolderOptions>>> _settingsActions = [];

    /// <summary>Defaults collected via <see cref="Defaults"/>, in call order.</summary>
    public IReadOnlyList<Action<TempFolderOptions>> DefaultsActions => _defaultsActions;

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<TempFolderOptions>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup first.</summary>
    public string? ConfigSectionPath { get; private set; }

    /// <summary>Cleanup-strategy override recorded via <see cref="ITempFolderBuilder.UseCleanupStrategy{T}"/>.</summary>
    public Type? CleanupStrategyType { get; private set; }

    /// <summary>Naming-strategy override recorded via <see cref="ITempFolderBuilder.UseNamingStrategy{T}"/>.</summary>
    public Type? NamingStrategyType { get; private set; }

    public ITempFolderBuilder Defaults(Action<TempFolderOptions> configureDefaults)
    {
        ArgumentNullException.ThrowIfNull(configureDefaults);
        _defaultsActions.Add(configureDefaults);
        return this;
    }

    public ITempFolderBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public ITempFolderBuilder Options(Action<OptionsBuilder<TempFolderOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }

    public ITempFolderBuilder UseCleanupStrategy<T>() where T : class, Cleanup.ICleanupStrategy
    {
        CleanupStrategyType = typeof(T);
        return this;
    }

    public ITempFolderBuilder UseNamingStrategy<T>() where T : class, Naming.IFolderNameStrategy
    {
        NamingStrategyType = typeof(T);
        return this;
    }
}
