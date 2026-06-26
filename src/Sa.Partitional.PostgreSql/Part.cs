using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Describes a partitioning granularity (root, day, month, year) used by <see cref="PgPartBy"/>.
/// Inherits from <see cref="Classes.Enumeration{T}"/> for safe, id-based lookup.
/// </summary>
/// <param name="Name">Unique display name of the partition kind.</param>
/// <param name="PartBy">The <see cref="PartByRange"/> that drives date-range computation.</param>
public sealed record Part(string Name, PartByRange PartBy): Enumeration<Part>(Name.GetHashCode(), Name)
{
    /// <summary>
    /// The identifier string for the root (unpartitioned) partition.
    /// </summary>
    public const string RootId = "root";

    /// <summary>
    /// The root partition instance — always uses <see cref="PartByRange.Day"/> as its range.
    /// </summary>
    public static readonly Part Root = new(RootId, PartByRange.Day);
}
