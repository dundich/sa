namespace Sa.HybridFileStorage;

/// <summary>
/// Результат пакетной операции с детализацией по каждому элементу.
/// </summary>
public sealed class BatchResult<T>
{
    /// <summary>Items that succeeded.</summary>
    public required IReadOnlyList<T> Succeeded { get; init; }
    /// <summary>Items that failed, with their errors.</summary>
    public required IReadOnlyList<BatchError> Failed { get; init; }
    /// <summary>Number of items in the batch.</summary>
    public int Total => Succeeded.Count + Failed.Count;
    /// <summary>Whether at least one item failed.</summary>
    public bool HasErrors => Failed.Count > 0;

    /// <summary>Throws <see cref="BatchOperationException{T}"/> when any item failed.</summary>
    /// <param name="message">Optional exception message.</param>
    public void ThrowIfHasErrors(string? message = null)
    {
        if (HasErrors)
        {
            throw new BatchOperationException<T>(message ?? "Batch operation completed with errors", this);
        }
    }
}
