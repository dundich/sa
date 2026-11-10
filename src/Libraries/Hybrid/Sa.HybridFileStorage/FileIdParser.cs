using System.Globalization;

namespace Sa.HybridFileStorage;

/// <summary>
/// Utility for parsing and constructing file IDs in the format
/// <c>"storageType://basket/tenantId/[timestamp/]filename"</c>.
/// </summary>
public static class FileIdParser
{
    /// <summary>
    /// The scheme separator used between storage type and path.
    /// </summary>
    public const string SchemeSeparator = "://";

    /// <summary>
    /// Tries to parse a file ID into its constituent parts.
    /// </summary>
    /// <param name="fileId">The file ID string to parse.</param>
    /// <param name="basket">When this method returns, contains the basket (scope) name, or empty string if parsing failed.</param>
    /// <param name="tenantId">When this method returns, contains the tenant identifier, or zero if parsing failed.</param>
    /// <param name="timestamp">When this method returns, contains the Unix timestamp (seconds), or zero if parsing failed.</param>
    /// <param name="fileName">When this method returns, contains the file name, or empty string if parsing failed.</param>
    /// <returns><c>true</c> if the file ID was parsed successfully; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This overload guesses the layout: a numeric second segment is read as the PostgreSQL
    /// timestamp, so <c>fs://share/1/2024/report.pdf</c> (a directory named <c>2024</c>) is taken
    /// as the timestamp format. A caller that knows the provider's layout should use
    /// <see cref="TryParse(string, FileIdFormat, out string, out int, out long, out string)"/>.
    /// </remarks>
    public static bool TryParse(
        string fileId,
        out string basket,
        out int tenantId,
        out long timestamp,
        out string fileName)
        => TryParse(fileId, FileIdFormat.Auto, out basket, out tenantId, out timestamp, out fileName);

    /// <summary>
    /// Tries to parse a file ID with the layout of a specific provider, so a numeric path segment
    /// is never mistaken for the PostgreSQL timestamp.
    /// </summary>
    /// <param name="fileId">The file ID string to parse.</param>
    /// <param name="format">The layout to parse: the heuristic, or the timestamp / no-timestamp shape.</param>
    /// <param name="basket">When this method returns, contains the basket (scope) name, or empty string if parsing failed.</param>
    /// <param name="tenantId">When this method returns, contains the tenant identifier, or zero if parsing failed.</param>
    /// <param name="timestamp">When this method returns, contains the Unix timestamp (seconds), or zero when the layout has none or parsing failed.</param>
    /// <param name="fileName">When this method returns, contains the file name, or empty string if parsing failed.</param>
    /// <returns><c>true</c> if the file ID matched <paramref name="format"/> and was parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse(
        string fileId,
        FileIdFormat format,
        out string basket,
        out int tenantId,
        out long timestamp,
        out string fileName)
    {
        tenantId = default;
        timestamp = default;
        fileName = string.Empty;
        basket = string.Empty;

        if (string.IsNullOrEmpty(fileId)) return false;

        ReadOnlySpan<char> span = fileId.AsSpan();
        int schemeEnd = span.IndexOf(SchemeSeparator.AsSpan());
        if (schemeEnd == -1) return false;

        var afterSpan = span[(schemeEnd + SchemeSeparator.Length)..];
        int scopeEnd = afterSpan.IndexOf('/');
        if (scopeEnd == -1) return false;

        basket = afterSpan[..scopeEnd].ToString();
        afterSpan = afterSpan[(scopeEnd + 1)..];

        // "tenantId/timestamp/filename" (Postgres) or "tenantId/filename" (S3/FileSystem/InMemory)
        int firstSlash = afterSpan.IndexOf('/');
        if (firstSlash == -1) return false;

        var firstPart = afterSpan[..firstSlash];
        if (!int.TryParse(firstPart, NumberStyles.None, CultureInfo.InvariantCulture, out tenantId))
            return false;

        var afterFirst = afterSpan[(firstSlash + 1)..];

        switch (format)
        {
            case FileIdFormat.WithTimestamp:
                {
                    int secondSlash = afterFirst.IndexOf('/');
                    if (secondSlash == -1) return false;

                    if (!long.TryParse(afterFirst[..secondSlash], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
                        return false;

                    fileName = afterFirst[(secondSlash + 1)..].ToString();
                    break;
                }

            case FileIdFormat.WithoutTimestamp:
                fileName = afterFirst.ToString();
                break;

            default:
                {
                    // Check if second part is a timestamp (Postgres) or filename (others)
                    int secondSlash = afterFirst.IndexOf('/');
                    if (secondSlash != -1 && long.TryParse(afterFirst[..secondSlash], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
                    {
                        // Postgres format: tenantId/timestamp/filename
                        fileName = afterFirst[(secondSlash + 1)..].ToString();
                    }
                    else
                    {
                        // Simple format: tenantId/filename
                        fileName = afterFirst.ToString();
                    }

                    break;
                }
        }

        return !string.IsNullOrEmpty(fileName);
    }

    /// <summary>
    /// Formats a file ID using the Postgres-compatible format with timestamp.
    /// </summary>
    /// <param name="storageType">The storage type identifier (e.g., "pg").</param>
    /// <param name="basket">The basket (scope) name.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="date">The date associated with the file.</param>
    /// <param name="fileName">The file name.</param>
    /// <returns>A formatted file ID string.</returns>
    public static string FormatToFileId(
        string storageType,
        string basket,
        int tenantId,
        DateTimeOffset date,
        string fileName)
            => $"{storageType}://{basket}/{tenantId}/{date.ToUnixTimeSeconds()}/{NormalizeFileName(fileName)}";

    /// <summary>
    /// Normalizes a file name by removing leading slashes/backslashes and converting backslashes to forward slashes.
    /// </summary>
    public static string NormalizeFileName(string fileName)
        => fileName.TrimStart('\\', '/').Replace('\\', '/');

    /// <summary>
    /// Extracts the file extension from a file name.
    /// </summary>
    public static string GetFileExtension(string fileName)
        => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant().TrimStart('.');
}
