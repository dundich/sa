using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Describes a partitioning granularity (root, day, month, year) used by <see cref="PgPartBy"/>.
/// Inherits from <see cref="Classes.Enumeration{T}"/> for safe, id-based lookup.
/// </summary>
/// <param name="Name">Unique display name of the partition kind.</param>
/// <param name="PartBy">The <see cref="PartByRange"/> that drives date-range computation.</param>
public sealed record Part(string Name, PartByRange PartBy): Enumeration<Part>(StableId(Name), Name)
{
    /// <summary>
    /// The identifier string for the root (unpartitioned) partition.
    /// </summary>
    public const string RootId = "root";

    /// <summary>
    /// The root partition instance — always uses <see cref="PartByRange.Day"/> as its range.
    /// </summary>
    public static readonly Part Root = new(RootId, PartByRange.Day);

    /// <summary>
    /// A process-independent 32-bit id (FNV-1a over the UTF-16 code units).
    /// </summary>
    /// <remarks>
    /// <c>string.GetHashCode()</c> is randomised per process on .NET, so an id taken from it means
    /// something different after every restart - fine as long as it never leaves the process, a trap
    /// the moment it is compared against, or stored, a value that came from another process. The
    /// only variant name that reaches storage is <see cref="Name"/>; the id stays in memory, and a
    /// stable one makes that true by construction rather than by convention.
    /// </remarks>
    private static int StableId(string name)
    {
        uint hash = 2166136261u; // FNV offset basis
        foreach (char c in name)
        {
            hash ^= c;
            hash *= 16777619u; // FNV prime
        }
        return unchecked((int)hash);
    }
}
