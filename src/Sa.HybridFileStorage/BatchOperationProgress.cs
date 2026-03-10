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
    public double PercentComplete => Total == 0 ? 0 : (Completed * 100.0 / Total);
}
