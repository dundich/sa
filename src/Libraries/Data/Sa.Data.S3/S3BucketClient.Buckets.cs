using Sa.Data.S3.Utils;
using System.Net;

namespace Sa.Data.S3;

/// <summary>
/// Функции управления бакетом
/// </summary>
public sealed partial class S3BucketClient : IBucketOperations
{
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
                Errors.UnexpectedResult(response);
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
                Errors.UnexpectedResult(response);
                return false;
        }
    }

    private async Task DeleteAllObjects(CancellationToken ct)
    {
        var prefixes = new List<string>();
        await foreach (var key in List(null, ct).ConfigureAwait(false))
        {
            prefixes.Add(key);
        }

        // S3 SelectObjectCancel требует удаления по ключам, не по prefix
        // Перечитаем всё по ключам и удалим
        foreach (var key in prefixes)
        {
            try
            {
                await DeleteFile(key, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore individual delete failures during force cleanup
            }
        }
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
                Errors.UnexpectedResult(response);
                return false;
        }
    }
}
