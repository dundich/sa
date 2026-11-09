using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace Sa.Data.S3;

/// <summary>
/// Обёртка над <see cref="HttpResponseMessage"/>, позволяющая удобно работать с загруженным файлом
/// </summary>
/// <remarks>
/// Владеет ответом сервера: <see cref="Dispose"/> освобождает его, а <see cref="GetStream"/> передаёт
/// владение потоку <see cref="S3Stream"/> (тот освобождает ответ вместе с собой). Достаточно
/// освободить что-то одно.
/// <para>
/// Отсутствующий файл — не ошибка: <see cref="Exists"/> вернёт <c>false</c>, а
/// <see cref="ContentType"/>/<see cref="Length"/> — <c>null</c>.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString()}")]
public sealed class S3File : IDisposable
{
    private readonly HttpResponseMessage _response;

    internal S3File(HttpResponseMessage response)
    {
        _response = response;
    }

    /// <summary>
    /// Тип файла в MIME
    /// </summary>
    /// <remarks>
    /// Берётся из заголовка "Content-Type" из <see cref="HttpResponseMessage" />; для
    /// отсутствующего файла — <c>null</c> (иначе вернулся бы тип тела ошибки).
    /// </remarks>
    public string? ContentType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Exists ? _response.Content.Headers.ContentType?.MediaType : null;
    }

    /// <summary>
    /// Файл существовал?
    /// </summary>
    public bool Exists
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Размер файла
    /// </summary>
    /// <remarks>
    /// Берётся из заголовка "Content-Length" из <see cref="HttpResponseMessage" />; для
    /// отсутствующего файла — <c>null</c> (иначе вернулась бы длина XML-описания ошибки).
    /// </remarks>
    public long? Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Exists ? _response.Content.Headers.ContentLength : null;
    }

    /// <summary>
    /// Ответ сервера
    /// </summary>
    public HttpStatusCode StatusCode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _response.StatusCode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator bool(S3File file)
    {
        return file._response.IsSuccessStatusCode;
    }

    public void Dispose()
    {
        _response.Dispose();
    }

    /// <summary>
    /// Возвращает Stream с данными файла из <see cref="HttpResponseMessage" />
    /// </summary>
    /// <returns>Stream of data</returns>
    /// <remarks>Когда Stream будет закрыт, <see cref="HttpResponseMessage" /> будет уничтожен</remarks>
    public async Task<Stream> GetStream(CancellationToken ct)
    {
        if (!_response.IsSuccessStatusCode)
        {
            return Stream.Null;
        }

        var stream = await _response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new S3Stream(_response, stream);
    }

    [ExcludeFromCodeCoverage]
    public override string ToString()
    {
        if (_response.IsSuccessStatusCode)
        {
            return $"OK (Length = {Length})";
        }

        var reasonPhrase = _response.ReasonPhrase;
        var statusCode = _response.StatusCode;
        return string.IsNullOrEmpty(reasonPhrase)
            ? $"{statusCode} ({(int)statusCode})"
            : $"{statusCode} ({(int)statusCode}, '{reasonPhrase}')";
    }
}
