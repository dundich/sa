namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Describes how the root table of a partition scheme comes into existence.
/// </summary>
public enum TableRootMode
{
    /// <summary>
    /// The library owns the root table: it emits <c>CREATE TABLE IF NOT EXISTS ... PARTITION BY</c>
    /// together with the primary-key constraint. This is the default behaviour.
    /// </summary>
    Create = 0,

    /// <summary>
    /// The root table already exists as a plain (non-partitioned) table. On the first migration the
    /// library converts it into a partitioned one — rename to a backup, create the partitioned root,
    /// move the rows — and from then on manages its partitions like a table it created itself.
    /// </summary>
    /// <remarks>
    /// Only tables without existing partitions are supported. A root that is already partitioned is
    /// rejected: partitions are expected to be created and owned by this library only.
    /// </remarks>
    Adopt = 1,
}
