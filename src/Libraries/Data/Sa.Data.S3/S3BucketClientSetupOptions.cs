using System.ComponentModel.DataAnnotations;

namespace Sa.Data.S3;

/// <summary>
/// Настройки клиента S3, обслуживаемые стандартным конвейером
/// <c>Microsoft.Extensions.Options</c>: <c>Configure</c> → <c>PostConfigure</c> (нормализация) →
/// валидация.
/// </summary>
/// <remarks>
/// Тип сам наследует <see cref="S3BucketSettings"/>, поэтому он же передаётся в конструктор
/// <see cref="S3BucketClient"/> — копирования настроек нет, а значит нет и целого класса ошибок
/// вида «в проекцию забыли перенести поле».
/// <para>
/// Класс открыт для наследования: провайдер <c>Sa.HybridFileStorage.S3</c> добавляет к нему своё
/// имя корзины и тип хранилища, чтобы у пользователя остался один тип опций на оба слоя.
/// </para>
/// </remarks>
public class S3BucketClientSetupOptions : S3BucketSettings
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
    /// Установите в <c>TimeSpan.FromHours(2)</c> для периодического пересоздания handler и
    /// освобождения устаревших соединений.
    /// </summary>
    /// <remarks>
    /// Значение применяется к именованному <c>HttpClient</c> фабрики. Сам
    /// <see cref="IS3BucketClient"/> в DI — синглтон и держит один <c>HttpClient</c>, поэтому
    /// пересоздание handler'а происходит только у клиентов, полученных из
    /// <c>IHttpClientFactory</c> напрямую; для готового соединения пересоздание даёт
    /// <see cref="ConnectionPoolLifetime"/>.
    /// </remarks>
    public TimeSpan HandlerLifetime { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Приводит значения к каноническому виду. Конвейер вызывает это до валидации, поэтому
    /// <see cref="Validate"/> всегда видит уже нормализованные значения.
    /// </summary>
    public virtual void Normalize()
    {
        // Конфигурация может дать null (например, "AccessKey": null в JSON): без этого проверка
        // ниже падала бы NullReferenceException вместо ValidationException с именем опции.
        AccessKey = (AccessKey ?? string.Empty).Trim();
        SecretKey = (SecretKey ?? string.Empty).Trim();
        Bucket = (Bucket ?? string.Empty).Trim();
        Region = (Region ?? string.Empty).Trim();
        Service = (Service ?? string.Empty).Trim();

        // Пробелы вокруг адреса делают Uri.TryCreate капризным, а хвостовой слеш не должен
        // считаться частью цели: «http://host:9000/» и «http://host:9000» — один и тот же
        // эндпоинт. Схему и authority приводим к нижнему регистру, query и fragment отбрасываем
        // (у эндпоинта их быть не может), хвостовой слеш срезаем. Пустое значение не трогаем —
        // его корректно отчитает Validate(), сообщив, какая опция не задана.
        if (!string.IsNullOrWhiteSpace(Endpoint))
        {
            Endpoint = Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out Uri? endpoint)
                ? $"{endpoint.Scheme.ToLowerInvariant()}://{endpoint.Authority}{endpoint.AbsolutePath.TrimEnd('/')}"
                : Endpoint.Trim();
        }
    }

    /// <summary>
    /// Проверяет настройки и выбрасывает <see cref="ValidationException"/> с текстом про первую
    /// найденную проблему.
    /// </summary>
    /// <exception cref="ValidationException">Выбрасывается, если настройки некорректны.</exception>
    /// <remarks>
    /// Идентификация бакета и эндпоинта проверяется базовым <see cref="S3BucketSettings.Validate"/>;
    /// здесь добавляются транспортные значения. Явные проверки вместо
    /// <c>ValidateDataAnnotations()</c>: тот помечен <c>RequiresUnreferencedCode</c> (IL2026) и
    /// ломает Native AOT.
    /// </remarks>
    public override void Validate()
    {
        base.Validate();

        if (TotalRequestTimeout <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"{GetType().Name}:{nameof(TotalRequestTimeout)} must be positive, " +
                $"but was {TotalRequestTimeout}.");
        }

        if (ConnectionPoolLifetime <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"{GetType().Name}:{nameof(ConnectionPoolLifetime)} must be positive, " +
                $"but was {ConnectionPoolLifetime}.");
        }

        // Timeout.InfiniteTimeSpan (-00:00:00.001) — осмысленное значение «не пересоздавать handler»,
        // поэтому нижняя граница именно оно, а не ноль. Строгое сравнение важно: при <= значение по
        // умолчанию отвергалось бы самим собой.
        if (HandlerLifetime < Timeout.InfiniteTimeSpan)
        {
            throw new ValidationException(
                $"{GetType().Name}:{nameof(HandlerLifetime)} must be positive or " +
                $"{nameof(Timeout)}.{nameof(Timeout.InfiniteTimeSpan)}, but was {HandlerLifetime}.");
        }
    }
}
