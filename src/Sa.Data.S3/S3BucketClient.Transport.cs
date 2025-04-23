using Sa.Data.S3.Utils;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

namespace Sa.Data.S3;

/// <summary>
/// Transport functions
/// </summary>
public sealed partial class S3BucketClient
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private HttpRequestMessage CreateRequest(HttpMethod method, string? fileName = null)
	{
		var url = new ValueStringBuilder(stackalloc char[512]);
		url.Append(_bucketUrl);

		// ReSharper disable once InvertIf
		if (!string.IsNullOrEmpty(fileName))
		{
			url.Append('/');
			HttpDescription.AppendEncodedName(ref url, fileName);
		}

		return new HttpRequestMessage(method, new Uri(url.Flush(), UriKind.Absolute));
	}

	private Task<HttpResponseMessage> Send(HttpRequestMessage request, string payloadHash, CancellationToken ct)
	{
		if (_disposed)
		{
			Errors.Disposed();
		}

		var now = DateTime.UtcNow;

		var headers = request.Headers;
		headers.Add("host", _host);
		headers.Add("x-amz-content-sha256", payloadHash);
		headers.Add("x-amz-date", now.ToString(Signature.Iso8601DateTime, CultureInfo.InvariantCulture));

		if (_useHttp2)
		{
			request.Version = HttpVersion.Version20;
		}

		var signature = _signature.Calculate(request, payloadHash, _s3Headers, now);
		headers.TryAddWithoutValidation("Authorization", _http.BuildHeader(now, signature));

		return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
	}
}
