using Microsoft.Extensions.Options;

namespace Sa.Media.FFmpeg;

/// <summary>
/// Реализация <see cref="IFFMpegBuilder"/> по умолчанию: запоминает два настраивающих
/// намерения — конфигурационный раздел и действия конвейера <c>Options(...)</c> — чтобы
/// <see cref="Setup.AddSaFFMpeg"/> воспроизвёл их в своём фиксированном слоте.
/// </summary>
internal sealed class FFMpegBuilder : IFFMpegBuilder
{
    /// <summary>Действия конвейера, собранные через <see cref="Options"/>; Setup воспроизводит их после привязки секции.</summary>
    private readonly List<Action<OptionsBuilder<FFMpegOptions>>> _settingsActions = [];

    /// <summary>Действия конвейера, собранные через <see cref="Options"/>, в порядке вызовов.</summary>
    public IReadOnlyList<Action<OptionsBuilder<FFMpegOptions>>> SettingsActions => _settingsActions;

    /// <summary>Секция, записанная через <see cref="FromConfiguration"/>; Setup биндит её до действий <see cref="Options"/>.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IFFMpegBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IFFMpegBuilder Options(Action<OptionsBuilder<FFMpegOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
