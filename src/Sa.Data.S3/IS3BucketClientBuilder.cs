using Microsoft.Extensions.Options;

namespace Sa.Data.S3;

/// <summary>
/// Собирает конфигурацию S3-клиента, регистрируемого через
/// <see cref="Setup.AddSaS3BucketClient"/>: стандартный конвейер опций
/// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) и конфигурационный раздел —
/// в одном делегате регистрации.
/// </summary>
/// <remarks>
/// Оба настраивающих намерения живут здесь — <see cref="FromConfiguration"/> для секции и
/// <see cref="Options"/> для конвейера, поэтому <c>AddSaS3BucketClient</c> принимает
/// единственный параметр <c>configure</c>. Секция биндится в фиксированном слоте до
/// действий <see cref="Options"/>, так что их <c>Configure</c> всегда важнее конфигурации —
/// где бы эти вызовы ни стояли в колбэке.
/// </remarks>
public interface IS3BucketClientBuilder
{
    /// <summary>
    /// Привязывает <see cref="S3BucketClientSetupOptions"/> из заданного
    /// конфигурационного раздела, например <c>"S3"</c> — секцию, которая иначе передаётся
    /// аргументом <c>configSectionPath</c> у <c>AddSaS3BucketClient</c>, перенесённую
    /// внутрь того же делегата регистрации.
    /// </summary>
    /// <remarks>
    /// Запоминается билдером; <c>AddSaS3BucketClient</c> биндит её в фиксированном слоте
    /// до действий <see cref="Options"/> — значит, их <c>Configure</c> всегда важнее
    /// конфигурации, где бы этот вызов ни стоял в колбэке. Можно вызывать несколько раз —
    /// побеждает последний путь.
    /// </remarks>
    /// <param name="configSectionPath">Путь к секции конфигурации, например <c>"S3"</c>.</param>
    IS3BucketClientBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Отдаёт колбэку стандартный <see cref="OptionsBuilder{TOptions}"/> для
    /// <see cref="S3BucketClientSetupOptions"/> — поверхность <c>Configure</c> /
    /// <c>PostConfigure</c> / <c>Validate</c> конвейера опций в том же канале регистрации,
    /// что и секция.
    /// </summary>
    /// <remarks>
    /// Выполняется в конвейере опций <b>после</b> <c>BindConfiguration</c>, вызванного для
    /// <see cref="FromConfiguration"/>, поэтому <c>Configure</c> здесь — тот escape hatch,
    /// что перебивает конфигурацию, а <c>Validate</c> добавляется к встроенным проверкам, а
    /// не заменяет их. Можно вызывать несколько раз — действия выполняются в порядке
    /// вызовов.
    /// </remarks>
    /// <param name="configureSettings">Колбэк, получающий билдер опций настроек.</param>
    IS3BucketClientBuilder Options(Action<OptionsBuilder<S3BucketClientSetupOptions>> configureSettings);
}
