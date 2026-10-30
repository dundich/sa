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
/// <see cref="S3BucketClientSetupOptions.Validate"/>, вызываемый после нормализации.
/// </para>
/// </remarks>
public class S3BucketSettings
{
    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the endpoint URL for the S3 service.
    /// Must be an absolute HTTP or HTTPS URL.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string Service { get; set; } = "s3";

    public bool UseHttp2 { get; set; }
}