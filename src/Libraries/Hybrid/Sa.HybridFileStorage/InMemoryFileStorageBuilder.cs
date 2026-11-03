using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage;

/// <summary>
/// Default <see cref="IInMemoryFileStorageBuilder"/>: records the two configuring intents —
/// the configuration section and the <c>Options(...)</c> pipeline actions — so
/// <see cref="Setup.AddSaInMemoryFileStorage"/> can replay them in its fixed slot.
/// </summary>
internal sealed class InMemoryFileStorageBuilder : IInMemoryFileStorageBuilder
{
    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<InMemoryFileStorageOptions>>> _settingsActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<InMemoryFileStorageOptions>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup before the <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IInMemoryFileStorageBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IInMemoryFileStorageBuilder Options(Action<OptionsBuilder<InMemoryFileStorageOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
