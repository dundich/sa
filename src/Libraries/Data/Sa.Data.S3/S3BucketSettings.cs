using System.ComponentModel.DataAnnotations;

namespace Sa.Data.S3;

/// <summary>
/// Represents the settings required to configure an S3 bucket connection.
/// </summary>
/// <remarks>
/// Базовая часть настроек клиента: всё, что идентифицирует бакет и цель. Транспортные
/// настройки (таймауты, время жизни handler'а) живут в
/// <see cref="S3BucketClientSetupOptions"/>, который наследует этот тип.
/// <para>
/// <c>required</c> убран намеренно: тип обслуживается конвейером
/// <c>Microsoft.Extensions.Options</c>, где значения приходят из конфигурации, а не из
/// объектного инициализатора, и компилятору нечего проверять. Заменяет его
/// <see cref="Validate"/>, который вызывают и конвейер опций, и конструктор
/// <see cref="S3BucketClient"/> — поэтому ручное создание клиента (без DI) получает ту же
/// проверку, а не <see cref="UriFormatException"/> из середины первого запроса.
/// </para>
/// </remarks>
public class S3BucketSettings
{
    /// <summary>
    /// Gets or sets the access key used to sign requests.
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the secret key used to sign requests.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the target bucket.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the endpoint URL for the S3 service.
    /// Must be an absolute HTTP or HTTPS URL.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the region used when signing requests.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Gets or sets the service name used when signing requests.
    /// </summary>
    public string Service { get; set; } = "s3";

    /// <summary>
    /// Gets or sets a value indicating whether HTTP/2 should be used for requests.
    /// </summary>
    public bool UseHttp2 { get; set; }

    /// <summary>
    /// Проверяет настройки и выбрасывает <see cref="ValidationException"/> с текстом про первую
    /// найденную проблему.
    /// </summary>
    /// <exception cref="ValidationException">Выбрасывается, если настройки некорректны.</exception>
    /// <remarks>
    /// Проверка <see cref="Endpoint"/> особенно важна: значение попадает в <c>new Uri(...)</c> внутри
    /// клиента, и без неё неверный адрес всплыл бы как голый <see cref="UriFormatException"/> без
    /// указания, какая опция виновата. Явные проверки вместо <c>ValidateDataAnnotations()</c>: тот
    /// помечен <c>RequiresUnreferencedCode</c> (IL2026) и ломает Native AOT.
    /// </remarks>
    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new ValidationException($"{GetType().Name}:{nameof(Endpoint)} cannot be empty.");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ValidationException(
                $"{GetType().Name}:{nameof(Endpoint)} must be an absolute http or https URL, " +
                $"but was '{Endpoint}'.");
        }

        RequireText(AccessKey, nameof(AccessKey));
        RequireText(SecretKey, nameof(SecretKey));
        RequireText(Bucket, nameof(Bucket));
        RequireText(Region, nameof(Region));
        RequireText(Service, nameof(Service));
    }

    private protected void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationException($"{GetType().Name}:{name} cannot be empty.");
        }
    }
}
