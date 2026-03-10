using System.Globalization;

namespace Sa.HybridFileStorage.Postgres;

internal static class FileIdParser
{
    public const string SchemeSeparator = "://";

    public static bool TryParseFileIdWithFilename(
        string fileId,
        out int tenantId,
        out long timestamp,
        out string fileName)
    {
        tenantId = default;
        timestamp = default;
        fileName = string.Empty;

        if (string.IsNullOrEmpty(fileId)) return false;

        ReadOnlySpan<char> span = fileId.AsSpan();
        int schemeEnd = span.IndexOf(SchemeSeparator.AsSpan());
        if (schemeEnd == -1) return false;

        var afterScheme = span[(schemeEnd + SchemeSeparator.Length)..];
        int tableEnd = afterScheme.IndexOf('/');
        if (tableEnd == -1) return false;
        afterScheme = afterScheme[(tableEnd + 1)..];

        int tenantEnd = afterScheme.IndexOf('/');
        if (tenantEnd == -1) return false;

        var tenantSpan = afterScheme[..tenantEnd];
        if (!int.TryParse(tenantSpan, NumberStyles.None, CultureInfo.InvariantCulture, out tenantId))
            return false;

        var afterTenant = afterScheme[(tenantEnd + 1)..];
        int timestampEnd = afterTenant.IndexOf('/');
        if (timestampEnd == -1) return false;

        var timestampSpan = afterTenant[..timestampEnd];
        if (!long.TryParse(timestampSpan, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
            return false;

        fileName = afterTenant[(timestampEnd + 1)..].ToString();
        return !string.IsNullOrEmpty(fileName);
    }


    public static string FormatToFileId(
        string storageType,
        string tableName,
        int tenantId,
        DateTimeOffset date,
        string fileName)
            => $"{storageType}://{tableName}/{tenantId}/{date.ToUnixTimeSeconds()}/{NormalizeFileName(fileName)}";

    public static string NormalizeFileName(string fileName) => fileName.TrimStart('\\', '/').Replace('\\', '/');

    public static string GetFileExtension(string fileName) => Path.GetExtension(fileName ?? string.Empty).ToLower().TrimStart('.');
}
