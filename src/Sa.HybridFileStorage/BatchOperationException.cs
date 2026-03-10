namespace Sa.HybridFileStorage;

/// <summary>
/// Исключение, агрегирующее ошибки пакетной операции.
/// </summary>
public sealed class BatchOperationException<T>(string message, BatchResult<T> result)
    : Exception(message, result.Failed.Count > 0 ? result.Failed[0].Exception : null)
{
    public BatchResult<T> Result { get; } = result;

    public override string ToString() =>
        $"{base.ToString()}\nFailed: {Result.Failed.Count}/{Result.Total}";
}
