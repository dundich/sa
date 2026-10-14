using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Default <see cref="IPostgresFileStorageBuilder"/>: records the two configuring intents —
/// the configuration section and the <c>Options(...)</c> pipeline actions — so
/// <see cref="Setup.AddSaPostgreSqlFileStorage"/> can replay them in its fixed slot.
/// </summary>
internal sealed class PostgresFileStorageBuilder : IPostgresFileStorageBuilder
{
    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<PostgresFileStorageOptions>>> _settingsActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<PostgresFileStorageOptions>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup before the <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IPostgresFileStorageBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IPostgresFileStorageBuilder Options(Action<OptionsBuilder<PostgresFileStorageOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
