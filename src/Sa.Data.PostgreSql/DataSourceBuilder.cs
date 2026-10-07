using Microsoft.Extensions.Options;

namespace Sa.Data.PostgreSql;

/// <summary>
/// Default <see cref="IDataSourceBuilder"/>: records the two configuring intents — the
/// configuration section and the <c>Options(...)</c> pipeline actions — so
/// <see cref="Setup.AddSaPostgreSqlDataSource"/> can replay them in its fixed slot.
/// </summary>
internal sealed class DataSourceBuilder : IDataSourceBuilder
{
    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<PgDataSourceOptions>>> _settingsActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<PgDataSourceOptions>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup before the <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IDataSourceBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IDataSourceBuilder Options(Action<OptionsBuilder<PgDataSourceOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
