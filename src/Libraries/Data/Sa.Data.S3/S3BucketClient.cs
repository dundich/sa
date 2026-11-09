using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Xml;
using Sa.Data.S3.Utils;

namespace Sa.Data.S3;

/// <summary>
/// Клиент для загрузки данных в S3 и их получения
/// </summary>
[DebuggerDisplay("Client for '{Bucket}'")]
public sealed partial class S3BucketClient : IS3BucketClient, IDisposable
{
    internal const int DefaultPartSize = 5 * 1024 * 1024; // 5 Mb

    private readonly IArrayPool _arrayPool;

    private readonly string _bucket;
    private readonly HttpClient _client;
    private readonly string _host;
    private readonly Signature _signature;
    private readonly HeadBuilder _headBuilder;
    private readonly bool _useHttp2;

    private readonly string _urlMiddle;
    private readonly string _urlStart;

    private readonly TimeProvider _timeProvider;

    private bool _disposed;

    public string Bucket { get; }
    public Uri Endpoint { get; }

    /// <param name="client">HTTP-клиент. В DI-пути приходит из <c>IHttpClientFactory</c>; при
    /// создании вручную владение переходит клиенту — <see cref="Dispose"/> его освободит.</param>
    /// <param name="settings">Настройки подключения. Проверяются <see cref="S3BucketSettings.Validate"/>,
    /// поэтому ручной путь получает ту же диагностику, что и конвейер опций.</param>
    /// <param name="timeProvider">Источник времени для подписи; по умолчанию системный.</param>
    /// <param name="arrayPool">Пул буферов; по умолчанию <see cref="DefaultArrayPool.Instance"/>.</param>
    public S3BucketClient(
        HttpClient client,
        S3BucketSettings settings,
        TimeProvider? timeProvider = null,
        IArrayPool? arrayPool = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);

        // Без DI конвейер опций не работает, поэтому здесь проверяется то же самое: иначе пустой
        // Bucket уводит запросы в корень эндпоинта, а неверный Endpoint всплывает голым
        // UriFormatException без указания опции.
        settings.Validate();

        Bucket = settings.Bucket;
        Endpoint = new Uri(settings.Endpoint, UriKind.Absolute);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _arrayPool = arrayPool ?? DefaultArrayPool.Instance;

        // TrimEnd: у эндпоинта без пути AbsoluteUri уже заканчивается на '/', а у проксированного
        // («https://host/s3») — нет. Без нормализации второго случая bucket приклеивался бы к
        // последнему сегменту пути: «https://host/s3bucket».
        _bucket = $"{Endpoint.AbsoluteUri.TrimEnd('/')}/{Bucket.ToLowerInvariant()}";
        _client = client;
        // Порт по умолчанию не входит в Host: так его отправляет и подписывает presigned-url путь.
        _host = Endpoint.IsDefaultPort ? Endpoint.Host : $"{Endpoint.Host}:{Endpoint.Port}";
        _useHttp2 = settings.UseHttp2;

        _urlMiddle = $"%2F{settings.Region}%2F{settings.Service}%2Faws4_request";
        _urlStart = $"?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential={settings.AccessKey}%2F";

        _signature = new Signature(settings.Region, settings.Service, settings.SecretKey, _arrayPool);
        _headBuilder = new HeadBuilder(settings.AccessKey, settings.Region, settings.Service);
    }

    /// <summary>Пул буферов клиента — его же использует <see cref="S3Upload"/>.</summary>
    internal IArrayPool ArrayPool => _arrayPool;

    /// <summary>
    /// Создаёт подписанную ссылку на файл без проверки наличия файла на сервере
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="expiration">Время жизни ссылки</param>
    /// <returns>Возвращает подписанную ссылку на файл</returns>
    public string BuildFileUrl(string fileName, TimeSpan expiration)
    {
        // X-Amz-Expires — целое число секунд в диапазоне 1..604800; вне его сервер ответит 403,
        // поэтому неверный аргумент отсекается здесь, а не посреди чужого запроса.
        if (expiration < TimeSpan.FromSeconds(1) || expiration > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiration),
                expiration,
                "The presigned URL lifetime must be between 1 second and 7 days.");
        }

        var now = _timeProvider.GetUtcNow();
        var url = BuildFileUrl(_bucket, fileName, now, expiration);
        return BuildSignedFileUrl(url, now);
    }

    /// <summary>
    /// Создаёт ссылку на файл без подписи (только путь) — годится лишь для публично читаемого
    /// бакета; для приватного используйте <see cref="BuildFileUrl(string, TimeSpan)"/> или
    /// <see cref="GetFileUrl"/>.
    /// </summary>
    public string BuildFileUrl(string fileName) => UrlUtils.BuildFileUrl(_bucket, fileName);

    /// <summary>
    /// Строит запрос presigned-ссылки: путь и query до подписи.
    /// </summary>
    internal string BuildFileUrl(string bucket, string fileName, DateTimeOffset now, TimeSpan expires)
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);

        builder.Append(bucket);
        builder.Append('/');
        UrlUtils.AppendEncodedName(ref builder, fileName);

        builder.Append(_urlStart);
        builder.Append(now, Signature.Iso8601Date);
        builder.Append(_urlMiddle);

        builder.Append("&X-Amz-Date=");
        builder.Append(now, Signature.Iso8601DateTime);
        builder.Append("&X-Amz-Expires=");
        builder.Append((int)expires.TotalSeconds);

        builder.Append("&X-Amz-SignedHeaders=host");

        return builder.Flush();
    }

    private string BuildSignedFileUrl(string url, DateTimeOffset now)
    {
        var signature = _signature.Calculate(url, now);
        return $"{url}&X-Amz-Signature={signature}";
    }

    public async Task DeleteFile(string fileName, CancellationToken ct)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Delete, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        if (response.StatusCode is not HttpStatusCode.NoContent)
        {
            await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
        }

        response.Dispose();
    }


    /// <summary>
    /// Получает объектное представление файла на сервере
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает объектное представление файла на сервере</returns>
    public async Task<S3File> GetFile(string fileName, CancellationToken ct)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Get, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        // Not found is not an error: S3File wraps the response and reports Exists == false. The
        // response must stay alive — disposing it here made S3File.Length/ContentType throw
        // ObjectDisposedException, because both read response.Content.
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound)
        {
            return new S3File(response);
        }

        await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
        return null!; // Errors.UnexpectedResult всегда бросает
    }

    public async Task<Stream> GetFileStream(string fileName, CancellationToken ct)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Get, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                // S3Stream освобождает ответ вместе с потоком — иначе HttpResponseMessage
                // переживал бы вызывающего и держал свои заголовки до сборки мусора.
                return new S3Stream(response, stream);
            case HttpStatusCode.NotFound:
                response.Dispose();
                return Stream.Null;
            default:
                await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
                return Stream.Null;
        }
    }

    /// <summary>
    /// Создаёт подписанную ссылку на файл после проверки наличия файла на сервере
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="expiration">Время жизни ссылки</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает подписанную ссылку на файл</returns>
    public async Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct)
    {
        return await IsFileExists(fileName, ct).ConfigureAwait(false)
            ? BuildFileUrl(fileName, expiration)
            : null;
    }

    public async Task<bool> IsFileExists(string fileName, CancellationToken ct)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Head, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                response.Dispose();
                return true;
            case HttpStatusCode.NotFound:
                response.Dispose();
                return false;
            default:
                await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
                return false;
        }
    }

    /// <summary>
    /// Перечисляет ключи бакета, автоматически продолжая страницы по
    /// <c>NextContinuationToken</c>.
    /// </summary>
    /// <remarks>
    /// ListObjectsV2 отдаёт не больше 1000 ключей за ответ, поэтому без цикла по токену всё, что
    /// сверх первой страницы, молча пропадало — и <see cref="DeleteBucket(bool, CancellationToken)"/>
    /// не мог очистить большой бакет.
    /// </remarks>
    public async IAsyncEnumerable<string> List(
        string? prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? continuationToken = null;

        do
        {
            var url = BuildListUrl(prefix, continuationToken);

            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
            }

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
                yield break;
            }

            string? nextToken = null;

            try
            {
                var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                await using (responseStream.ConfigureAwait(false))
                {
                    using var reader = XmlStreamReader.CreateReader(responseStream);

                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();

                        if (reader.NodeType is not XmlNodeType.Element)
                        {
                            continue;
                        }

                        switch (reader.LocalName)
                        {
                            case "Key":
                                yield return await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                break;
                            case "NextContinuationToken":
                                nextToken = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                break;
                        }
                    }
                }
            }
            finally
            {
                // Итератор может быть брошен раньше (break/FirstAsync/отмена) — ответ не должен
                // переживать перечислитель, иначе соединение висит до сборки мусора.
                response.Dispose();
            }

            continuationToken = string.IsNullOrEmpty(nextToken) ? null : nextToken;
        }
        while (continuationToken is not null);
    }

    /// <summary>
    /// Загружает файл с ручным управлением загрузкой блоков файла
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="contentType">Тип загружаемого файла</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <returns>Возвращает объект управления загрузкой</returns>
    public async Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct)
    {
        return await MultipartStart(fileName, contentType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Загружает файл на сервер
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="contentType">Тип загружаемого файла</param>
    /// <param name="data">Данные файл</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <remarks>Если файл превышает 5 МБ, то будет применена Multipart-загрузка</remarks>
    /// <returns>Возвращает результат загрузки файла</returns>
    public Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct)
    {
        var length = data.TryGetLength();

        // Пустой поток — обычный PUT нулевой длины, как и пустой массив: multipart из нуля частей
        // завершить нельзя, поэтому раньше такой вызов создавал загрузку, прерывал её и возвращал
        // false.
        return length is { } known && known <= DefaultPartSize
            ? PutFile(fileName, contentType, data, ct)
            : ExecuteMultipartUpload(fileName, contentType, data, ct);
    }

    /// <summary>
    /// Загружает файл на сервер
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="contentType">Тип загружаемого файла</param>
    /// <param name="data">Данные файл</param>
    /// <param name="ct">Токен отмены операции</param>
    /// <remarks>Если файл превышает 5 МБ, то будет применена Multipart-загрузка</remarks>
    /// <returns>Возвращает результат загрузки файла</returns>
    public Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct)
    {
        var length = data.Length;

        return length > DefaultPartSize
            ? ExecuteMultipartUpload(fileName, contentType, data, ct)
            : PutFile(fileName, contentType, data, data.Length, ct);
    }

    private async Task<bool> PutFile(
        string fileName,
        string contentType,
        Stream data,
        CancellationToken ct)
    {
        // Размер известен: сюда попадают только seekable-потоки (TryGetLength вернул длину),
        // иначе ветка была бы multipart.
        var buffer = _arrayPool.Rent<byte>((int)data.Length);

        try
        {
            // ReadTo читает до заполнения буфера: один ReadAsync имеет право вернуть меньше
            // запрошенного, и прежняя версия загружала молча усечённый объект.
            var dataSize = await data.ReadTo(buffer, ct).ConfigureAwait(false);

            var payloadHash = HashHelper.GetPayloadHash(buffer.AsSpan(0, dataSize));

            HttpResponseMessage response;
            using (var request = CreateRequest(HttpMethod.Put, fileName))
            {
                using var content = new ByteArrayContent(buffer, 0, dataSize);
                content.Headers.Add("content-type", contentType);
                request.Content = content;

                response = await Send(request, payloadHash, ct).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                response.Dispose();
                return true;
            }

            await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _arrayPool.Return(buffer);
        }
    }

    private async Task<bool> PutFile(
        string fileName,
        string contentType,
        byte[] data,
        int length,
        CancellationToken ct)
    {
        var payloadHash = HashHelper.GetPayloadHash(data);

        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Put, fileName))
        {
            using var content = new ByteArrayContent(data, 0, length);
            content.Headers.Add("content-type", contentType);
            request.Content = content;

            response = await Send(request, payloadHash, ct).ConfigureAwait(false);
        }

        if (response.StatusCode is HttpStatusCode.OK)
        {
            response.Dispose();
            return true;
        }

        await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
        return false;
    }

    private string BuildListUrl(string? prefix, string? continuationToken)
    {
        var builder = new ValueStringBuilder(stackalloc char[256]);
        builder.Append(_bucket);
        builder.Append("?list-type=2");

        // Значения кодируются правилами query, а не пути: «/» в префиксе обязан стать «%2F»,
        // иначе сервер посчитает другую каноническую строку и вернёт SignatureDoesNotMatch.
        if (!string.IsNullOrEmpty(prefix))
        {
            builder.Append("&prefix=");
            UrlUtils.AppendEncodedQueryValue(ref builder, prefix);
        }

        if (continuationToken is not null)
        {
            builder.Append("&continuation-token=");
            UrlUtils.AppendEncodedQueryValue(ref builder, continuationToken);
        }

        return builder.Flush();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client.Dispose();
            _disposed = true;
        }
    }
}
