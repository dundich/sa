using Microsoft.Extensions.Options;

namespace Sa.Data.S3;

/// <summary>
/// Валидирует <see cref="S3BucketClientSetupOptions"/> для конвейера options.
/// </summary>
/// <remarks>
/// Регистрируется в <see cref="Setup.AddSaS3BucketClient"/> вместе с <c>ValidateOnStart()</c>,
/// поэтому неверная настройка всплывает как <see cref="OptionsValidationException"/> на старте
/// хоста, а не посреди загрузки файла. Сам <see cref="S3BucketClientSetupOptions.Validate"/>
/// сохраняет собственный тип исключения, чтобы те же проверки работали и вне DI.
/// </remarks>
internal sealed class S3BucketClientSetupOptionsValidator : IValidateOptions<S3BucketClientSetupOptions>
{
    /// <summary>
    /// Проверяет экземпляр опций с указанным именем.
    /// </summary>
    /// <param name="name">Имя проверяемого экземпляра опций.</param>
    /// <param name="options">Проверяемый экземпляр опций.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Skip"/>, если <paramref name="options"/> — <c>null</c>;
    /// <see cref="ValidateOptionsResult.Success"/>, если всё в порядке; иначе — ошибка с текстом
    /// сообщения.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, S3BucketClientSetupOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Skip;
        }

        try
        {
            options.Validate();
        }
        catch (System.ComponentModel.DataAnnotations.ValidationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }

        return ValidateOptionsResult.Success;
    }
}
