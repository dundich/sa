using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Default <see cref="IS3FileStorageBuilder"/>: records the two configuring intents — the
/// configuration section and the <c>Options(...)</c> pipeline actions — so
/// <see cref="Setup.AddSaS3FileStorage"/> can replay them in its fixed slot.
/// </summary>
internal sealed class S3FileStorageBuilder : IS3FileStorageBuilder
{
    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<S3FileStorageOptions>>> _settingsActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<S3FileStorageOptions>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup before the <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IS3FileStorageBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IS3FileStorageBuilder Options(Action<OptionsBuilder<S3FileStorageOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
