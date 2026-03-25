using System.Globalization;

namespace Sa.HybridFileStorage.Postgres;

internal static class FileIdParser
{
    public const string SchemeSeparator = "://";

    public static bool TryParse(
        string fileId,
        out string scopeName,
        out int tenantId,
        out long timestamp,
        out string fileName)
    {
        tenantId = default;
        timestamp = default;
        fileName = string.Empty;
        scopeName = string.Empty;

        if (string.IsNullOrEmpty(fileId)) return false;

        ReadOnlySpan<char> span = fileId.AsSpan();
        int schemeEnd = span.IndexOf(SchemeSeparator.AsSpan());
        if (schemeEnd == -1) return false;

        var afterSpan = span[(schemeEnd + SchemeSeparator.Length)..];
        int scopeEnd = afterSpan.IndexOf('/');
        if (scopeEnd == -1) return false;

        scopeName = afterSpan[..scopeEnd].ToString();

        afterSpan = afterSpan[(scopeEnd + 1)..];


        int tenantEnd = afterSpan.IndexOf('/');
        if (tenantEnd == -1) return false;

        var tenantSpan = afterSpan[..tenantEnd];
        if (!int.TryParse(tenantSpan, NumberStyles.None, CultureInfo.InvariantCulture, out tenantId))
            return false;

        var afterTenant = afterSpan[(tenantEnd + 1)..];
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
        string scopeName,
        int tenantId,
        DateTimeOffset date,
        string fileName)
            => $"{storageType}://{scopeName}/{tenantId}/{date.ToUnixTimeSeconds()}/{NormalizeFileName(fileName)}";

    public static string NormalizeFileName(string fileName)
        => fileName.TrimStart('\\', '/').Replace('\\', '/');

    public static string GetFileExtension(string fileName)
        => Path.GetExtension(fileName ?? string.Empty).ToLower().TrimStart('.');
}
