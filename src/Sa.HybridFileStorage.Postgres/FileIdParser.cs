using System.Globalization;

namespace Sa.HybridFileStorage.Postgres;

internal static class FileIdParser
{
    private const string DateFormat = "yyyy/MM/dd/HH";

    public static (int tenantId, long timestamp) ParseFromFileId(string fileId, string tableName)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("File ID cannot be null or empty.", nameof(fileId));
        }

        ReadOnlySpan<char> span = fileId.AsSpan();

        string seporator = $"://{tableName}/";

        int separatorIndex = span.IndexOf(seporator);
        if (separatorIndex == -1)
        {
            throw new FormatException("Invalid file ID format.");
        }

        ReadOnlySpan<char> subParts = span[(separatorIndex + seporator.Length)..]; // +3 for skip "://files/"

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


    public static string FormatToFileId(string storageType, string tableName, int tenantId, DateTimeOffset date, string fileName)
        => $"{storageType}://{tableName}/{tenantId}/{date.ToString(DateFormat, CultureInfo.InvariantCulture)}/{NormalizeFileName(fileName)}";

    public static string NormalizeFileName(string fileName) => fileName.TrimStart('\\', '/').Replace('\\', '/');

    public static string GetFileExtension(string fileName) => Path.GetExtension(fileName ?? string.Empty).ToLower().TrimStart('.');
}
