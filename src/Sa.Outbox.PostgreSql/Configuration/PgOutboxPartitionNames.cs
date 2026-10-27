using Sa.Partitional.PostgreSql;
using System.Text;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// The 63-byte identifier budget of an Outbox partition name.
/// </summary>
/// <remarks>
/// <para>
/// The partitions are created by <c>Sa.Partitional.PostgreSql</c>, which composes a leaf name as
/// <c>table{sep}v1{sep}v2{sep}dateSuffix</c> and refuses a name over PostgreSQL's 63-byte limit. The
/// Outbox supplies that name from three inputs an operator controls independently — the base table
/// name, the consumer group id and the message part name — so the budget has to be checked where those
/// inputs are known, with a message that names the one that does not fit. Without this the first
/// failing DDL is a delivery-log partition, i.e. it fires on the first *failed* delivery, long after
/// startup and exactly when the system is already having a bad day.
/// </para>
/// <para>
/// The separator and the suffix length are not copied by hand: the separator is the partitional
/// default (pinned by a test that reads it back from the built table settings), and the suffix is
/// rendered by <see cref="PgPartBy"/> itself.
/// </para>
/// </remarks>
internal static class PgOutboxPartitionNames
{
    /// <summary>PostgreSQL truncates identifiers to 63 <b>bytes</b> (UTF-8).</summary>
    public const int MaxIdentifierBytes = 63;

    /// <summary>
    /// Separator between the parts of a partition name. Must equal
    /// <see cref="PgOutboxTableSettings.Defaults.SqlPartSeparator"/> — the Outbox never configures a
    /// custom one, and <c>PgOutboxPartitionNameBudgetTests.Separator_MatchesThePartitionalDefault</c>
    /// asserts the two agree with what <c>Sa.Partitional.PostgreSql</c> actually uses.
    /// </summary>
    public const string Separator = PgOutboxTableSettings.Defaults.SqlPartSeparator;

    /// <summary>Byte length of the range suffix this module partitions by (day).</summary>
    private static int DaySuffixBytes { get; } =
        Encoding.UTF8.GetByteCount(PgPartBy.Day.Fmt(DateTimeOffset.UnixEpoch));

    /// <summary>
    /// Bytes available for the partition key values of a leaf of <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">The unqualified table name, as configured.</param>
    /// <param name="valueCount">How many partition key values the leaf carries.</param>
    public static int ValueBudget(string tableName, int valueCount)
        => MaxIdentifierBytes
           - Encoding.UTF8.GetByteCount(tableName)
           - (valueCount + 1) * Encoding.UTF8.GetByteCount(Separator)
           - DaySuffixBytes;

    /// <summary>
    /// Throws when the partition key values of <paramref name="tableName"/> would produce a name over
    /// PostgreSQL's limit, naming the value that does not fit.
    /// </summary>
    /// <param name="tableName">The unqualified table name, as configured.</param>
    /// <param name="what">
    /// What the values are, in the caller's terms — <c>"consumer group"</c>, <c>"message part"</c>.
    /// </param>
    /// <param name="values">The partition key values, in the order the name joins them.</param>
    /// <exception cref="InvalidOperationException">The composed name would be truncated by PostgreSQL.</exception>
    public static void EnsureFits(string tableName, string what, ReadOnlySpan<string> values)
    {
        int budget = ValueBudget(tableName, values.Length);
        int needed = 0;

        foreach (string value in values)
            needed += Encoding.UTF8.GetByteCount(value);

        if (needed <= budget) return;

        int overhead = MaxIdentifierBytes - budget;

        string detail = values.Length == 1
            ? $"'{values[0]}'"
            : string.Join(", ", values.ToArray().Select(v => $"'{v}' ({Encoding.UTF8.GetByteCount(v)} bytes)"));

        throw new InvalidOperationException(
            $"The partition name of table '{tableName}' would be {overhead + needed} bytes, over "
            + $"PostgreSQL's {MaxIdentifierBytes}-byte identifier limit. '{tableName}', "
            + $"{values.Length + 1} separators '{Separator}' and the day suffix take {overhead} bytes, "
            + $"leaving {budget} for the partition key values ({what}), which take {needed}: {detail}. "
            + "Shorten them or configure a shorter base table name.");
    }
}
