using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Default <see cref="IFileSystemStorageBuilder"/>: records the configuring intents — the
/// configuration section, the <c>Options(...)</c> pipeline actions and the temp-folder channel —
/// so <see cref="Setup.AddSaFileSystemFileStorage"/> can replay them in its fixed slot.
/// </summary>
internal sealed class FileSystemStorageBuilder : IFileSystemStorageBuilder
{
    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<FileSystemStorageOptions>>> _settingsActions = [];

    /// <summary>Temp-folder actions collected via <see cref="TempFolder"/>, in call order.</summary>
    private readonly List<Action<ITempFolderBuilder>> _tempFolderActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<FileSystemStorageOptions>>> SettingsActions => _settingsActions;

    /// <summary>Temp-folder actions collected via <see cref="TempFolder"/>, in call order.</summary>
    public IReadOnlyList<Action<ITempFolderBuilder>> TempFolderActions => _tempFolderActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup before the <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IFileSystemStorageBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IFileSystemStorageBuilder Options(Action<OptionsBuilder<FileSystemStorageOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }

    public IFileSystemStorageBuilder TempFolder(Action<ITempFolderBuilder> configureTempFolder)
    {
        ArgumentNullException.ThrowIfNull(configureTempFolder);
        _tempFolderActions.Add(configureTempFolder);
        return this;
    }
}
