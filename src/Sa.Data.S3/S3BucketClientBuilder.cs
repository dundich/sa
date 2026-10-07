using Microsoft.Extensions.Options;

namespace Sa.Data.S3;

/// <summary>
/// Реализация <see cref="IS3BucketClientBuilder"/> по умолчанию: запоминает два
/// настраивающих намерения — конфигурационный раздел и действия конвейера
/// <c>Options(...)</c> — чтобы <see cref="Setup.AddSaS3BucketClient"/> воспроизвёл их в
/// своём фиксированном слоте.
/// </summary>
internal sealed class S3BucketClientBuilder : IS3BucketClientBuilder
{
    /// <summary>Действия конвейера, собранные через <see cref="Options"/>; Setup воспроизводит их после привязки секции.</summary>
    private readonly List<Action<OptionsBuilder<S3BucketClientSetupOptions>>> _settingsActions = [];

    /// <summary>Действия конвейера, собранные через <see cref="Options"/>, в порядке вызовов.</summary>
    public IReadOnlyList<Action<OptionsBuilder<S3BucketClientSetupOptions>>> SettingsActions => _settingsActions;

    /// <summary>Секция, записанная через <see cref="FromConfiguration"/>; Setup биндит её до действий <see cref="Options"/>.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IS3BucketClientBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IS3BucketClientBuilder Options(Action<OptionsBuilder<S3BucketClientSetupOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
