namespace Sa.Data.S3;

/// <summary>
/// Операции над бакетом.
/// </summary>
public interface IBucketOperations
{
    /// <summary>
    /// Создаёт бакет.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns><c>true</c>, если бакет создан; <c>false</c>, если он уже существует.</returns>
    Task<bool> CreateBucket(CancellationToken ct);

    /// <summary>
    /// Удаляет пустой бакет.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns><c>true</c>, если бакет удалён; <c>false</c>, если его не было.</returns>
    Task<bool> DeleteBucket(CancellationToken ct);

    /// <summary>
    /// Удаляет бакет, при необходимости предварительно очистив его.
    /// </summary>
    /// <param name="forceDelete">
    /// Если <c>true</c>, сначала удаляются все объекты бакета (пачками по 1000).
    /// </param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>
    /// <c>true</c>, если бакет удалён; <c>false</c>, если его не было (в том числе при
    /// <paramref name="forceDelete"/> — контракт одинаков для обоих режимов).
    /// </returns>
    Task<bool> DeleteBucket(bool forceDelete, CancellationToken ct);

    /// <summary>
    /// Проверяет существование бакета.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns><c>true</c>, если бакет существует.</returns>
    Task<bool> IsBucketExists(CancellationToken ct);
}
