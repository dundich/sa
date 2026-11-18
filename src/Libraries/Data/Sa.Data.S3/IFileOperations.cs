namespace Sa.Data.S3;

/// <summary>
/// Операции над объектами бакета.
/// </summary>
public interface IFileOperations
{
    /// <summary>
    /// Строит URL объекта без подписи. Годится только для публично читаемого бакета — для
    /// приватного используйте <see cref="BuildFileUrl(string, TimeSpan)"/> или
    /// <see cref="GetFileUrlAsync"/>.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    string BuildFileUrl(string fileName);

    /// <summary>
    /// Строит presigned-ссылку на объект, не проверяя его наличие.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="expiration">Время жизни ссылки (S3 принимает не больше 7 суток).</param>
    string BuildFileUrl(string fileName, TimeSpan expiration);

    /// <summary>
    /// Удаляет объект. Успешное удаление несуществующего объекта — не ошибка (S3 отвечает 204).
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task DeleteFileAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Возвращает объектную обёртку над ответом; отсутствующий объект не считается ошибкой —
    /// у результата <see cref="S3File.Exists"/> будет <c>false</c>.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<S3File> GetFileAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Открывает поток чтения объекта. Для отсутствующего объекта возвращается
    /// <see cref="Stream.Null"/>.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<Stream> GetFileStreamAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Возвращает presigned-ссылку, если объект существует, иначе <c>null</c>.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="expiration">Время жизни ссылки.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<string?> GetFileUrlAsync(string fileName, TimeSpan expiration, CancellationToken ct);

    /// <summary>
    /// Проверяет существование объекта.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<bool> IsFileExistsAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Перечисляет ключи объекта, продолжая страницы ListObjectsV2 автоматически.
    /// </summary>
    /// <param name="prefix">Префикс ключа; <c>null</c> или пустая строка — весь бакет.</param>
    /// <param name="ct">Токен отмены операции.</param>
    IAsyncEnumerable<string> List(string? prefix, CancellationToken ct);

    /// <summary>
    /// Загружает массив байтов; данные больше 5 МБ уходят multipart-загрузкой.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="contentType">MIME-тип объекта.</param>
    /// <param name="data">Содержимое.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<bool> UploadFileAsync(string fileName, string contentType, byte[] data, CancellationToken ct);

    /// <summary>
    /// Начинает multipart-загрузку с ручным управлением частями.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="contentType">MIME-тип объекта.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<S3Upload> UploadFileAsync(string fileName, string contentType, CancellationToken ct);

    /// <summary>
    /// Загружает поток; данные больше 5 МБ (или поток неизвестной длины) уходят multipart-загрузкой.
    /// </summary>
    /// <param name="fileName">Ключ объекта.</param>
    /// <param name="contentType">MIME-тип объекта.</param>
    /// <param name="data">Содержимое.</param>
    /// <param name="ct">Токен отмены операции.</param>
    Task<bool> UploadFileAsync(string fileName, string contentType, Stream data, CancellationToken ct);
}
