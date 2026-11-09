using Sa.Data.S3.Utils;
using System.Net;
using System.Text;

namespace Sa.Data.S3;

/// <summary>
/// Функции управления бакетом
/// </summary>
public sealed partial class S3BucketClient : IBucketOperations
{
    /// <summary>DeleteObjects принимает не больше 1000 ключей за запрос.</summary>
    private const int MaxKeysPerDeleteRequest = 1000;

    public async Task<bool> CreateBucket(CancellationToken ct)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Put))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                response.Dispose();
                return true;
            case HttpStatusCode.Conflict: // already exists
                response.Dispose();
                return false;
            default:
                await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
                return false;
        }
    }

    public async Task<bool> DeleteBucket(CancellationToken ct)
    {
        return await DeleteBucket(forceDelete: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Удаляет бакет. Если forceDelete=true — сначала удаляет все объекты из бакета.
    /// </summary>
    /// <param name="forceDelete">Если true, предварительно удалит все объекты в бакете</param>
    /// <param name="ct">Токен отмены операции</param>
    public async Task<bool> DeleteBucket(bool forceDelete, CancellationToken ct)
    {
        if (forceDelete)
        {
            // Тот же контракт, что и у обычного удаления: отсутствующий бакет — это false, а не
            // 404 из листинга (раньше force-путь бросал HttpRequestException).
            if (!await IsBucketExists(ct).ConfigureAwait(false))
            {
                return false;
            }

            await DeleteAllObjects(ct).ConfigureAwait(false);
        }

        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Delete))
        {
            response = await Send(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent:
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
    /// Удаляет все объекты бакета пачками по <see cref="MaxKeysPerDeleteRequest"/>.
    /// </summary>
    /// <remarks>
    /// Пачка удаляется одним <c>POST ?delete</c> вместо запроса на каждый ключ. Листинг читается
    /// заново каждую итерацию и обрывается после первой пачки, поэтому память ограничена размером
    /// пачки, а продолжение страниц не зависит от того, что часть ключей уже удалена.
    /// </remarks>
    private async Task DeleteAllObjects(CancellationToken ct)
    {
        var batch = new List<string>(MaxKeysPerDeleteRequest);

        while (true)
        {
            batch.Clear();

            await foreach (var key in List(null, ct).ConfigureAwait(false))
            {
                batch.Add(key);

                if (batch.Count == MaxKeysPerDeleteRequest)
                {
                    break;
                }
            }

            if (batch.Count == 0)
            {
                return;
            }

            await DeleteObjects(batch, ct).ConfigureAwait(false);
        }
    }

    private async Task DeleteObjects(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var data = BuildDeleteObjectsBody(keys);
        var payload = Encoding.UTF8.GetBytes(data);

        var payloadHash = HashHelper.GetPayloadHash(payload);

        HttpResponseMessage response;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{_bucket}?delete"))
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.Add("content-type", "application/xml");
            // Content-MD5 обязателен для DeleteObjects. Заголовок не входит в подписываемые
            // (signature подписывает host/x-amz-*), но S3 проверяет его отдельно.
            content.Headers.Add("content-md5", HashHelper.GetMd5Base64(payload));
            request.Content = content;

            response = await Send(request, payloadHash, ct).ConfigureAwait(false);
        }

        if (response.StatusCode is not HttpStatusCode.OK)
        {
            await Errors.UnexpectedResult(response, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            await using (responseStream.ConfigureAwait(false))
            {
                // Успешные удаления приходят как <Deleted>, ошибки — как <Error><Code>. Статус при
                // частичном отказе всё равно 200, поэтому без этой проверки цикл очистки крутился
                // бы на тех же ключах бесконечно.
                var errorCode = await XmlStreamReader
                    .ReadStringAsync(responseStream, "Code", ct)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(errorCode))
                {
                    throw new HttpRequestException(
                        $"DeleteObjects failed for a batch of {keys.Count} keys: {errorCode}. " +
                        "The bucket was not emptied.");
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Тело DeleteObjects. Ключи экранируются: их присылает сервер, и «&amp;»/«&lt;» в имени
    /// объекта сделали бы документ невалидным.
    /// </summary>
    private static string BuildDeleteObjectsBody(IReadOnlyList<string> keys)
    {
        var builder = StringUtils.GetBuilder();

        builder.Append("<Delete xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");

        foreach (var key in keys)
        {
            builder.Append("<Object><Key>");
            StringUtils.AppendXmlEscaped(builder, key);
            builder.Append("</Key></Object>");
        }

        return builder.Append("</Delete>").Flush();
    }

    public async Task<bool> IsBucketExists(CancellationToken ct)
    {
        HttpResponseMessage response;
        using (var request = CreateRequest(HttpMethod.Head))
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
}
