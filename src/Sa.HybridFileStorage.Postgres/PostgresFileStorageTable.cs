namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// The DDL of the file table, kept out of the registration extension so the column types are
/// reviewable in one place and cannot drift between the root table and its partitions.
/// </summary>
internal static class PostgresFileStorageTable
{
    /// <summary>
    /// The column holding the file size in bytes.
    /// <para>
    /// <c>BIGINT</c>, not <c>INT</c>: the provider writes the stream length as a 64-bit value, and
    /// an <c>INT</c> column silently truncated (or overflowed) anything over 2 GB.
    /// </para>
    /// </summary>
    public const string SizeColumnType = "BIGINT";

    /// <summary>
    /// The column holding the upload timestamp, as Unix seconds.
    /// <para>
    /// Not declared here: the partitioning layer adds the range column as
    /// <c>"&lt;name&gt;" bigint NOT NULL</c> when a table does not declare it itself, and
    /// <c>PartByRange</c> names it. Declaring it with a different type would make the
    /// partitioning DDL and this table disagree.
    /// </para>
    /// </summary>
    public const string PartByRangeFieldName = "created_at";

    /// <summary>
    /// Gets the column definitions of the root table, in declaration order.
    /// <para>
    /// <c>id</c> is a bare <c>TEXT</c> holding the full file ID and participates in the primary
    /// key together with the partition key, so it is intentionally not narrowed to
    /// <c>VARCHAR</c>. <c>name</c> and <c>file_ext</c> are bounded because they come from
    /// caller-supplied file names. <c>size</c> is <see cref="SizeColumnType"/>.
    /// </para>
    /// </summary>
    public static string[] Columns { get; } =
    [
        "id TEXT NOT NULL",
        "name VARCHAR(512) NOT NULL",
        $"size {SizeColumnType} NOT NULL",
        "file_ext VARCHAR(64) NOT NULL",
        "tenant_id INT NOT NULL",
        "basket VARCHAR(63) NOT NULL",
        "data BYTEA NOT NULL",
    ];
}
