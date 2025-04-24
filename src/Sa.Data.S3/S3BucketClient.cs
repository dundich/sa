using Sa.Data.S3.Utils;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;


namespace Sa.Data.S3;

/// <summary>
/// Клиент для загрузки данных в S3 и их получения
/// </summary>
/// <seealso href="https://github.com/teoadal/Storage"/>
[DebuggerDisplay("Client for '{Bucket}'")]
public sealed partial class S3BucketClient : IDisposable, IS3BucketClient
{
    internal const int DefaultPartSize = 5 * 1024 * 1024; // 5 Mb

    internal readonly string Bucket;

    private static readonly string[] _s3Headers = // trimmed, lower invariant, ordered
    [
        "host",
        "x-amz-content-sha256",
        "x-amz-date",
    ];

    private readonly string _bucketUrl;
    private readonly string _host;
    private readonly HttpDescription _http;
    private readonly HttpClient _client;
    private readonly Signature _signature;
    private readonly bool _useHttp2;

    private bool _disposed;

    public S3BucketClient(HttpClient client, S3BucketClientSettings settings)
    {
        Bucket = settings.Bucket ?? throw new ArgumentException(nameof(settings.Bucket));

        var endpoint = new Uri(settings.Endpoint);

        _bucketUrl = $"{endpoint.AbsoluteUri}{Bucket.ToLowerInvariant()}";
        _client = client;
        _host = $"{endpoint.Host}:{endpoint.Port}";
        _http = new HttpDescription(settings.AccessKey, settings.Region, settings.Service, _s3Headers);
        _signature = new Signature(settings.SecretKey, settings.Region, settings.Service);
        _useHttp2 = settings.UseHttp2;
    }

    /// <summary>
    /// Создаёт подписанную ссылку на файл без проверки наличия файла на сервере
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="expiration">Время жизни ссылки</param>
    /// <returns>Возвращает подписанную ссылку на файл</returns>
    public string BuildFileUrl(string fileName, TimeSpan expiration)
    {
        var now = DateTime.UtcNow;
        var url = _http.BuildUrl(_bucketUrl, fileName, now, expiration);
        var signature = _signature.Calculate(url, now);

        return $"{url}&X-Amz-Signature={signature}";
    }

    public string BuildFileUrl(string fileName)
    {
        return HttpDescription.BuildUrl(_bucketUrl, fileName);
    }

    public async Task DeleteFile(string fileName, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Delete, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode is not HttpStatusCode.NoContent)
        {
            Errors.UnexpectedResult(response);
        }

        response.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();

        _disposed = true;
    }

    /// <summary>
    /// Получает объектное представление файла на сервере
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Возвращает объектное представление файла на сервере</returns>
    public async Task<S3File> GetFile(string fileName, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Get, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return new S3File(response);
            case HttpStatusCode.NotFound:
                response.Dispose();
                return new S3File(response);
            default:
                Errors.UnexpectedResult(response);
                return new S3File(null!); // никогда не будет вызвано
        }
    }

    public async Task<Stream> GetFileStream(string fileName, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Get, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            case HttpStatusCode.NotFound:
                response.Dispose();
                return Stream.Null;
            default:
                Errors.UnexpectedResult(response);
                return Stream.Null;
        }
    }

    /// <summary>
    /// Создаёт подписанную ссылку на файл после проверки наличия файла на сервере
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="expiration">Время жизни ссылки</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Возвращает подписанную ссылку на файл</returns>
    public async Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken cancellationToken)
    {
        return await IsFileExists(fileName, cancellationToken).ConfigureAwait(false)
            ? BuildFileUrl(fileName, expiration)
            : null;
    }

    public async Task<bool> IsFileExists(string fileName, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Head, fileName))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
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
                Errors.UnexpectedResult(response);
                return false;
        }
    }

    public async IAsyncEnumerable<string> List(string? prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(prefix)
            ? $"{_bucketUrl}?list-type=2"
            : $"{_bucketUrl}?list-type=2&prefix={HttpDescription.EncodeName(prefix)}";

        HttpResponseMessage response;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode is HttpStatusCode.OK)
        {
            var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            while (responseStream.CanRead)
            {
                var readString = XmlStreamReader.ReadString(responseStream, "Key");
                if (string.IsNullOrEmpty(readString))
                {
                    break;
                }

                yield return readString;
            }

            await responseStream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();

            yield break;
        }

        Errors.UnexpectedResult(response);
    }

    /// <summary>
    /// Загружает файл с ручным управлением загрузкой блоков файла
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="contentType">Тип загружаемого файла</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Возвращает объект управления загрузкой</returns>
    public async Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken cancellationToken)
    {
        var encodedFileName = HttpDescription.EncodeName(fileName);
        var uploadId = await MultipartStart(encodedFileName, contentType, cancellationToken).ConfigureAwait(false);

        return new S3Upload(this, fileName, encodedFileName, uploadId);
    }

    /// <summary>
    /// Загружает файл на сервер
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="contentType">Тип загружаемого файла</param>
    /// <param name="data">Данные файл</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <remarks>Если файл превышает 5 МБ, то будет применена Multipart-загрузка</remarks>
    /// <returns>Возвращает результат загрузки файла</returns>
    public Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken cancellationToken)
    {
        var length = data.TryGetLength();

        return length is null or 0 or > DefaultPartSize
            ? ExecuteMultipartUpload(fileName, contentType, data, cancellationToken)
            : PutFile(fileName, contentType, data, cancellationToken);
    }

    /// <summary>
    /// Загружает файл на сервер
    /// </summary>
    /// <param name="fileName">Название файла</param>
    /// <param name="contentType">Тип загружаемого файла</param>
    /// <param name="data">Данные файл</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <remarks>Если файл превышает 5 МБ, то будет применена Multipart-загрузка</remarks>
    /// <returns>Возвращает результат загрузки файла</returns>
    public Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken cancellationToken)
    {
        var length = data.Length;

        return length > DefaultPartSize
            ? ExecuteMultipartUpload(fileName, contentType, data, cancellationToken)
            : PutFile(fileName, contentType, data, data.Length, cancellationToken);
    }

    private async Task<bool> PutFile(
        string fileName,
        string contentType,
        Stream data,
        CancellationToken cancellationToken)
    {
        var bufferPool = ArrayPool<byte>.Shared;

        var buffer = bufferPool.Rent((int)data.Length); // размер точно есть
        var dataSize = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        var payloadHash = HashHelper.GetPayloadHash(buffer.AsSpan(0, dataSize));

        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Put, fileName))
        {
            using var content = new ByteArrayContent(buffer, 0, dataSize);
            content.Headers.Add("content-type", contentType);
            request.Content = content;

            try
            {
                response = await Send(request, payloadHash, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                bufferPool.Return(buffer);
            }
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Dispose();
            return true;
        }

        Errors.UnexpectedResult(response);
        return false;
    }

    private async Task<bool> PutFile(
        string fileName,
        string contentType,
        byte[] data,
        int length,
        CancellationToken cancellationToken)
    {
        var payloadHash = HashHelper.GetPayloadHash(data);

        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Put, fileName))
        {
            using var content = new ByteArrayContent(data, 0, length);
            content.Headers.Add("content-type", contentType);
            request.Content = content;

            response = await Send(request, payloadHash, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode is HttpStatusCode.OK)
        {
            response.Dispose();
            return true;
        }

        Errors.UnexpectedResult(response);
        return false;
    }

}
