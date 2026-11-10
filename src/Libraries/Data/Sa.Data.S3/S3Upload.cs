using Sa.Data.S3.Utils;
using System.Runtime.CompilerServices;

namespace Sa.Data.S3;

/// <summary>
/// Структура управления Multipart-загрузкой
/// </summary>
/// <remarks>
/// Синхронный <see cref="Dispose"/> освобождает только локальные буферы: прервать загрузку он не
/// может (это сетевой вызов), поэтому после него на сервере остаются неполные части. Используйте
/// <see cref="DisposeAsync"/> (или явный <see cref="AbortAsync"/>) — он прерывает незавершённую загрузку,
/// если она не была завершена.
/// </remarks>
public sealed class S3Upload : IDisposable, IAsyncDisposable
{
    private readonly S3BucketClient _client;
    private readonly string _encodedFileName;

    private byte[]? _byteBuffer;
    private bool _aborted;
    private bool _completed;
    private bool _disposed;
    private int _partCount;
    private readonly IArrayPool _arrayPool;
    private string[] _parts;


    internal S3Upload(S3BucketClient client, string fileName, string encodedFileName, string uploadId)
    {
        FileName = fileName;
        UploadId = uploadId;

        _client = client;
        _encodedFileName = encodedFileName;

        // Пул берётся у клиента: IArrayPool — публичный интерфейс, и подменённый пул должен
        // работать на всём пути загрузки, а не только в S3BucketClient.
        _arrayPool = client.ArrayPool;
        _parts = _arrayPool.Rent<string>(16);
    }

    /// <summary>
    /// Название загружаемого файла
    /// </summary>
    public string FileName
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    /// <summary>
    /// Идентификатор загрузки
    /// </summary>
    public string UploadId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    /// <summary>
    /// Сколько байт загружено
    /// </summary>
    public long Written
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        private set;
    }

    /// <summary>
    /// Прерывает загрузку и удалить временные данные с сервера
    /// </summary>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает результат отмены загрузки</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task<bool> AbortAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            Errors.Disposed();
        }

        _aborted = true;
        return _client.MultipartAbort(_encodedFileName, UploadId, ct);
    }

    /// <summary>
    /// Загружает блок данных на сервер
    /// </summary>
    /// <param name="data">Блок данных</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает результат загрузки</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task<bool> AddPartAsync(byte[] data, CancellationToken ct)
    {
        return AddPartAsync(data, 0, data.Length, ct);
    }

    /// <summary>
    /// Загружает блок данных указанного размера на сервер
    /// </summary>
    /// <param name="data">Блок данных</param>
    /// <param name="length">Количество данных, которые нужно взять из блока</param>
    /// <param name="ct">Токен отмены</param>
    /// <returns>Возвращает результат загрузки</returns>
    public Task<bool> AddPartAsync(byte[] data, int length, CancellationToken ct)
    {
        return AddPartAsync(data, 0, length, ct);
    }

    /// <summary>
    /// Загружает часть, начиная с <paramref name="offset"/>. Отдельный срез нужен, чтобы
    /// <see cref="AddPartsAsync(byte[], CancellationToken)"/> не копировал данные в промежуточный буфер.
    /// </summary>
    internal async Task<bool> AddPartAsync(byte[] data, int offset, int length, CancellationToken ct)
    {
        if (_disposed)
        {
            Errors.Disposed();
        }

        var partId = await _client.MultipartPutPart(
            _encodedFileName, UploadId, _partCount + 1, data, offset, length, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(partId))
        {
            return false;
        }

        if (_parts.Length == _partCount)
        {
            // clear: true — старый массив уходит в общий пул, и ссылки на ETag не должны в нём
            // оставаться.
            CollectionUtils.Resize(ref _parts, _arrayPool, _partCount * 2, clear: true);
        }

        _parts[_partCount++] = partId;

        Written += length;

        return true;
    }

    /// <summary>
    /// Разделяет данные на блоки и загружает их на сервер
    /// </summary>
    /// <param name="data">Блок данных</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает результат загрузки</returns>
    public async Task<bool> AddPartsAsync(Stream data, CancellationToken ct)
    {
        _byteBuffer ??= _arrayPool.Rent<byte>(S3BucketClient.DefaultPartSize);

        while (true)
        {
            var written = await data.ReadToAsync(_byteBuffer, ct).ConfigureAwait(false);
            if (written is 0)
            {
                break;
            }

            if (!await AddPartAsync(_byteBuffer, written, ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Разделяет данные на блоки и загружает их на сервер
    /// </summary>
    /// <param name="data">Блок данных</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает результат загрузки</returns>
    /// <remarks>
    /// Блоки — срезы <paramref name="data"/>, без копирования в промежуточный буфер: исходный
    /// массив не должен меняться до завершения вызова.
    /// </remarks>
    public async Task<bool> AddPartsAsync(byte[] data, CancellationToken ct)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var partSize = Math.Min(S3BucketClient.DefaultPartSize, data.Length - offset);

            if (!await AddPartAsync(data, offset, partSize, ct).ConfigureAwait(false))
            {
                return false;
            }

            offset += partSize;
        }

        return true;
    }

    /// <summary>
    /// Указывает серверу, что загрузка завершена и блоки данных можно объединить в файл
    /// </summary>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает результат завершения загрузки</returns>
    public async Task<bool> CompleteAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            Errors.Disposed();
        }

        if (_partCount == 0)
        {
            return false;
        }

        _completed = await _client
            .MultipartComplete(_encodedFileName, UploadId, _parts, _partCount, ct)
            .ConfigureAwait(false);

        return _completed;
    }

    /// <summary>Aborts the multipart upload and releases the request resources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Array.Clear(_parts, 0, _partCount);
        _arrayPool.ReturnToPool(_parts, clear: true);
        _parts = null!;

        if (_byteBuffer is not null)
        {
            _arrayPool.ReturnToPool(_byteBuffer);
            _byteBuffer = null;
        }

        _disposed = true;
    }

    /// <summary>
    /// Освобождает ресурсы и прерывает незавершённую загрузку, чтобы на сервере не оставались
    /// неполные части.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed && !_completed && !_aborted)
        {
            try
            {
                await AbortAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Освобождение не должно бросать: сервер мог быть уже недоступен.
            }
        }

        Dispose();
    }
}
