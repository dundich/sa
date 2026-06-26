using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Contract for supplying list-partition values to <see cref="ITableBuilder.AddMigration"/>.
/// Implement this interface when partition values depend on runtime data (e.g. reading from another table).
/// </summary>
public interface IPartTableMigrationSupport
{
    /// <summary>
    /// Returns a two-dimensional array of partition values. The outer array represents groups; the inner array represents individual values within each group.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Partition values resolved at runtime.</returns>
    Task<StrOrNum[][]> GetParts(CancellationToken cancellationToken);
}
