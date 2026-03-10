namespace Sa.HybridFileStorage;

/// <summary>
/// Опции для пакетных операций.
/// </summary>
public sealed class BatchOptions
{
    /// <summary>
    /// Максимальное количество параллельных операций. По умолчанию: 4.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Продолжать выполнение при ошибках в отдельных элементах. По умолчанию: true.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// Таймаут на одну операцию (нулевое значение = без таймаута).
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Прогресс-коллбэк: сообщает о завершении каждой операции (успех/ошибка).
    /// </summary>
    public IProgress<BatchOperationProgress>? Progress { get; set; }
}
