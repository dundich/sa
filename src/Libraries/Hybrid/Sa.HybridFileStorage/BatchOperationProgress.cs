namespace Sa.HybridFileStorage;

/// <summary>
/// Информация о прогрессе пакетной операции.
/// </summary>
public readonly record struct BatchOperationProgress(
    int Total,
    int Completed,
    int Succeeded,
    int Failed,
    string? FileId = null,
    Exception? Error = null)
{
    /// <summary>Completed share of the batch, in the range 0-100.</summary>
    public double PercentComplete => Total == 0 ? 0 : (Completed * 100.0 / Total);
}
