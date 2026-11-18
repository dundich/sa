namespace Sa.HybridFileStorage;

/// <summary>
/// The layout of a file ID's path, used by
/// <see cref="FileIdParser.TryParse(string, FileIdFormat, out string, out int, out long, out string)"/>
/// to read a file ID without guessing.
/// </summary>
public enum FileIdFormat
{
    /// <summary>
    /// Guess the layout: a numeric second path segment is read as the PostgreSQL timestamp.
    /// Convenient for tooling, but ambiguous when a no-timestamp provider stores a file in a
    /// directory named with a number (<c>fs://share/1/2024/report.pdf</c>).
    /// </summary>
    Auto = 0,

    /// <summary>
    /// <c>{basket}/{tenantId}/{timestamp}/{fileName}</c> — the layout of the PostgreSQL provider.
    /// The file ID is not parsed when the timestamp segment is missing or not a number.
    /// </summary>
    WithTimestamp = 1,

    /// <summary>
    /// <c>{basket}/{tenantId}/{fileName}</c> — the layout of the in-memory, filesystem and S3
    /// providers. Everything after the tenant segment is the file name, even when it contains
    /// numeric directories.
    /// </summary>
    WithoutTimestamp = 2,
}
