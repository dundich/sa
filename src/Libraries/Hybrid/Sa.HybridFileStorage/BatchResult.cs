namespace Sa.HybridFileStorage;

/// <summary>
/// Результат пакетной операции с детализацией по каждому элементу.
/// </summary>
public sealed class BatchResult<T>
{
    public required IReadOnlyList<T> Succeeded { get; init; }
    public required IReadOnlyList<BatchError> Failed { get; init; }
    public int Total => Succeeded.Count + Failed.Count;
    public bool HasErrors => Failed.Count > 0;

    public void ThrowIfHasErrors(string? message = null)
    {
        if (HasErrors)
        {
            throw new BatchOperationException<T>(message ?? "Batch operation completed with errors", this);
        }
    }
}
