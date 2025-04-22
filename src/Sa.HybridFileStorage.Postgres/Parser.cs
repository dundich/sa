using System.Globalization;

namespace Sa.HybridFileStorage.Postgres;

public static class Parser
{
    private const string DateFormat = "yyyy/MM/dd/HH";

    public static (int tenantId, long timestamp) ParseFromFileId(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("File ID cannot be null or empty.", nameof(fileId));
        }

        ReadOnlySpan<char> span = fileId.AsSpan();

        int separatorIndex = span.IndexOf("://");
        if (separatorIndex == -1)
        {
            throw new FormatException("Invalid file ID format.");
        }

        ReadOnlySpan<char> subParts = span[(separatorIndex + 3)..]; // +3 for skip "://"

        int firstSlashIndex = subParts.IndexOf('/');
        if (firstSlashIndex == -1)
        {
            throw new FormatException("Invalid tenant ID format in file ID.");
        }

        ReadOnlySpan<char> tenantIdSpan = subParts[..firstSlashIndex];
        if (!int.TryParse(tenantIdSpan, out int tenantId))
        {
            throw new FormatException("Invalid tenant ID format in file ID.");
        }

        ReadOnlySpan<char> dateSpan = subParts.Slice(firstSlashIndex + 1, DateFormat.Length);

        if (!DateTimeOffset.TryParseExact(dateSpan, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
        {
            throw new FormatException("Invalid timestamp format in file ID.");
        }

        long timestamp = date.ToUnixTimeSeconds();
        return (tenantId, timestamp);
    }


    public static string FormatToFileId(string storageType, int tenantId, DateTimeOffset date, string fileName)
        => $"{storageType}://{tenantId}/{date.ToString(DateFormat, CultureInfo.InvariantCulture)}/{NormalizeFileName(fileName)}";

    public static string NormalizeFileName(string fileName) => fileName.TrimStart('\\', '/').Replace('\\', '/');

    public static string GetFileExtension(string fileName) => Path.GetExtension(fileName ?? string.Empty).ToLower().TrimStart('.');
}
