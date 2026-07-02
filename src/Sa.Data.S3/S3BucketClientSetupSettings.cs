namespace Sa.Data.S3;

public sealed class S3BucketClientSetupSettings : S3BucketSettings
{
    /// <summary>
    /// Максимальное время ожидания ответа сервера для каждого запроса. По умолчанию: 180 секунд.
    /// </summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Время жизни пула соединений в SocketsHttpHandler. По умолчанию: 15 минут.
    /// </summary>
    public TimeSpan ConnectionPoolLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Время жизни обработчика HttpClient. По умолчанию: бесконечность (для long-running сервисов).
    /// Установите в TimeSpan.FromHours(2) для периодического пересоздания handler и освобождения stale connections.
    /// </summary>
    public TimeSpan HandlerLifetime { get; set; } = Timeout.InfiniteTimeSpan;
}
