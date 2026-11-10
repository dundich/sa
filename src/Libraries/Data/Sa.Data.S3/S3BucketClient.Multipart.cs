using Sa.Data.S3.Utils;
using System.Net;
using System.Text;

namespace Sa.Data.S3;

/// <summary>
/// Функции управления multipart-загрузкой
/// </summary>
public partial class S3BucketClient
{
    internal async Task<bool> MultipartAbort(string encodedFileName, string uploadId, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        using (var request = new HttpRequestMessage(HttpMethod.Delete, BuildMultipartUrl(encodedFileName, uploadId)))
        {
            try
            {
                response = await SendAsync(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        if (response is null)
        {
            return false;
        }

        var result = response is
        {
            IsSuccessStatusCode: true,
            StatusCode: HttpStatusCode.NoContent,
        };

        response.Dispose();

        return result;
    }

    internal async Task<bool> MultipartComplete(
        string encodedFileName,
        string uploadId,
        string[] partTags,
        int tagsCount,
        CancellationToken ct)
    {
        var builder = StringUtils.GetBuilder();

        builder.Append("<CompleteMultipartUpload>");
        for (var i = 0; i < partTags.Length; i++)
        {
            if (i == tagsCount)
            {
                break;
            }

            builder.Append("<Part>");
            builder.Append("<PartNumber>", i + 1, "</PartNumber>");
            builder.Append("<ETag>");
            StringUtils.AppendXmlEscaped(builder, partTags[i]);
            builder.Append("</ETag>");
            builder.Append("</Part>");
        }

        var data = builder
            .Append("</CompleteMultipartUpload>")
            .Flush();

        var payloadHash = HashHelper.GetPayloadHash(data);

        HttpResponseMessage response;
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post,
                   BuildMultipartUrl(encodedFileName, uploadId)))
        {
            using var content = new StringContent(data, Encoding.UTF8);
            request.Content = content;
            response = await SendAsync(request, payloadHash, ct).ConfigureAwait(false);
        }

        var result = response is { IsSuccessStatusCode: true, StatusCode: HttpStatusCode.OK };

        response.Dispose();
        return result;
    }

    internal async Task<string?> MultipartPutPart(
        string encodedFileName,
        string uploadId,
        int partNumber,
        byte[] partData,
        int offset,
        int partSize,
        CancellationToken ct)
    {
        var payloadHash = HashHelper.GetPayloadHash(partData.AsSpan(offset, partSize));

        var url = $"{_bucket}/{encodedFileName}?partNumber={partNumber}&uploadId={UrlUtils.EncodeQueryValue(uploadId)}";

        HttpResponseMessage response;
        using (var request = new HttpRequestMessage(HttpMethod.Put, url))
        {
            // Content-Length выводит сам ByteArrayContent — ручной заголовок был лишним.
            using var content = new ByteArrayContent(partData, offset, partSize);
            request.Content = content;

            response = await SendAsync(request, payloadHash, ct).ConfigureAwait(false);
        }

        var result = response is { IsSuccessStatusCode: true, StatusCode: HttpStatusCode.OK }
            ? response.Headers.ETag?.Tag
            : null;

        response.Dispose();
        return result;
    }

    private async Task<bool> ExecuteMultipartUpload(
        string fileName,
        string contentType,
        Stream data,
        CancellationToken ct)
    {
        // DisposeAsync прерывает незавершённую загрузку: забытый Abort иначе оставлял бы на сервере
        // оплачиваемые неполные части.
        await using var upload = await UploadFileAsync(fileName, contentType, ct).ConfigureAwait(false);

        if (await upload.AddPartsAsync(data, ct).ConfigureAwait(false) && await upload.CompleteAsync(ct).ConfigureAwait(false))
        {
            return true;
        }

        await upload.AbortAsync(ct).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> ExecuteMultipartUpload(
        string fileName,
        string contentType,
        byte[] data,
        CancellationToken ct)
    {
        await using var upload = await UploadFileAsync(fileName, contentType, ct).ConfigureAwait(false);

        if (await upload.AddPartsAsync(data, ct).ConfigureAwait(false) && await upload.CompleteAsync(ct).ConfigureAwait(false))
        {
            return true;
        }

        await upload.AbortAsync(ct).ConfigureAwait(false);
        return false;
    }

    private async Task<S3Upload> MultipartStart(string fileName, string contentType, CancellationToken ct)
    {
        var encodedFileName = UrlUtils.UrlEncodeName(fileName);

        HttpResponseMessage response;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{_bucket}/{encodedFileName}?uploads"))
        {
            using var content = new ByteArrayContent([]);
            content.Headers.Add("content-type", contentType);
            request.Content = content;

            response = await SendAsync(request, HashHelper.EmptyPayloadHash, ct).ConfigureAwait(false);
        }

        if (response.StatusCode is HttpStatusCode.OK)
        {
            var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            string uploadId;
            await using (responseStream.ConfigureAwait(false))
            {
                uploadId = await XmlStreamReader.ReadStringAsync(responseStream, "UploadId", ct).ConfigureAwait(false);
            }

            response.Dispose();

            return new S3Upload(this, fileName, encodedFileName, uploadId);
        }

        await Errors.UnexpectedResultAsync(response, ct).ConfigureAwait(false);

        return null!; // Errors.UnexpectedResult всегда бросает
    }

    /// <summary>
    /// URL multipart-операции. <paramref name="uploadId"/> кодируется как значение query: id —
    /// opaque-строка (у AWS это base64 с <c>+</c>, <c>/</c> и <c>=</c>), и без кодирования сервер
    /// видел бы другую строку, чем та, по которой мы посчитали подпись.
    /// </summary>
    private string BuildMultipartUrl(string encodedFileName, string uploadId)
        => $"{_bucket}/{encodedFileName}?uploadId={UrlUtils.EncodeQueryValue(uploadId)}";
}
