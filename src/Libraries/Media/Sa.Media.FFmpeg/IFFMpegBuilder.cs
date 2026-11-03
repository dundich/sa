using Microsoft.Extensions.Options;

namespace Sa.Media.FFmpeg;

/// <summary>
/// Собирает конфигурацию FFmpeg, регистрируемого через <see cref="Setup.AddSaFFMpeg"/>:
/// стандартный конвейер опций (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>)
/// и конфигурационный раздел — в одном делегате регистрации.
/// </summary>
/// <remarks>
/// Оба настраивающих намерения живут здесь — <see cref="FromConfiguration"/> для секции и
/// <see cref="Options"/> для конвейера, поэтому <c>AddSaFFMpeg</c> принимает единственный
/// параметр <c>configure</c>. Секция биндится в фиксированном слоте до действий
/// <see cref="Options"/>, так что их <c>Configure</c> всегда важнее конфигурации —
/// где бы эти вызовы ни стояли в колбэке.
/// </remarks>
public interface IFFMpegBuilder
{
    /// <summary>
    /// Привязывает <see cref="FFMpegOptions"/> из заданного конфигурационного раздела,
    /// например <c>"Ffmpeg"</c> — секцию, которая иначе передаётся аргументом
    /// <c>configSectionPath</c> у <c>AddSaFFMpeg</c>, перенесённую внутрь того же
    /// делегата регистрации.
    /// </summary>
    /// <remarks>
    /// Запоминается билдером; <c>AddSaFFMpeg</c> биндит её в фиксированном слоте до
    /// действий <see cref="Options"/> — значит, их <c>Configure</c> всегда важнее
    /// конфигурации, где бы этот вызов ни стоял в колбэке. Можно вызывать несколько
    /// раз — побеждает последний путь.
    /// </remarks>
    /// <param name="configSectionPath">Путь к секции конфигурации, например <c>"Ffmpeg"</c>.</param>
    IFFMpegBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Отдаёт колбэку стандартный <see cref="OptionsBuilder{TOptions}"/> для
    /// <see cref="FFMpegOptions"/> — поверхность <c>Configure</c> / <c>PostConfigure</c> /
    /// <c>Validate</c> конвейера опций в том же канале регистрации, что и секция.
    /// </summary>
    /// <remarks>
    /// Выполняется в конвейере опций <b>после</b> <c>BindConfiguration</c>, вызванного для
    /// <see cref="FromConfiguration"/>, поэтому <c>Configure</c> здесь — тот escape hatch,
    /// что перебивает конфигурацию, а <c>Validate</c> добавляется к встроенным проверкам,
    /// а не заменяет их. Можно вызывать несколько раз — действия выполняются в порядке
    /// вызовов.
    /// </remarks>
    /// <param name="configureSettings">Колбэк, получающий билдер опций настроек.</param>
    IFFMpegBuilder Options(Action<OptionsBuilder<FFMpegOptions>> configureSettings);
}
