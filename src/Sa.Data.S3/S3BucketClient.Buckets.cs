using Sa.Data.S3.Utils;
using System.Net;

namespace Sa.Data.S3;

/// <summary>
/// Функции управления бакетом
/// </summary>
public sealed partial class S3BucketClient
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
