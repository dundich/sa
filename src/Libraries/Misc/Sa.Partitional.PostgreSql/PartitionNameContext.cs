using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Input passed to the <see cref="ITableBuilder.WithLongPartitionName"/> callback when the regular
/// partition name would exceed PostgreSQL's 63-byte identifier limit.
/// </summary>
/// <param name="RootTableName">The root table name, without schema and without quotes.</param>
/// <param name="Separator">The configured partition separator (default <c>__</c>).</param>
/// <param name="Values">
/// The list-partition values of this partition, outermost level first. Empty for a range-only leaf.
/// </param>
/// <param name="RangeSuffix">
/// The range suffix (<c>y2026m10d29</c>) or the default-partition token (<c>def$</c>); <c>null</c>
/// for a list level without a range leaf.
/// </param>
/// <param name="IsDefaultPartition">Whether this name belongs to a <c>DEFAULT</c> partition.</param>
/// <param name="IdealName">The regular name the library would have used.</param>
/// <param name="IdealBytes">The UTF-8 byte length of <see cref="IdealName"/>.</param>
public readonly record struct PartitionNameContext(
    string RootTableName,
    string Separator,
    IReadOnlyList<StrOrNum> Values,
    string? RangeSuffix,
    bool IsDefaultPartition,
    string IdealName,
    int IdealBytes)
{
    /// <summary>PostgreSQL's identifier limit in bytes.</summary>
    public const int MaxIdentifierBytes = 63;

    /// <summary>How many bytes <see cref="IdealName"/> is over the limit.</summary>
    public int ExcessBytes => IdealBytes - MaxIdentifierBytes;
}
