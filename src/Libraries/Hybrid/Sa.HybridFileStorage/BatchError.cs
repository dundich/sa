namespace Sa.HybridFileStorage;

/// <summary>
/// Ошибка обработки отдельного элемента в пакетной операции.
/// </summary>
public readonly record struct BatchError(string FileId, Exception Exception, int Index);
