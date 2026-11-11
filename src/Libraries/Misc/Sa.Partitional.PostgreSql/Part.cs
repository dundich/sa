namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Describes a partitioning granularity (root, day, month, year) used by <see cref="PgPartBy"/>.
/// Inherits from <see cref="Enumeration{T}"/> for safe, id-based lookup.
/// </summary>
public sealed record Part : Enumeration<Part>
{
    /// <summary>The <see cref="PartByRange"/> that drives date-range computation.</summary>
    public required PartByRange PartBy { get; init; }

    /// <summary>
    /// The identifier string for the root (unpartitioned) partition.
    /// </summary>
    public const string RootId = "root";

    /// <summary>
    /// The root partition instance — always uses <see cref="PartByRange.Day"/> as its range.
    /// </summary>
    public static readonly Part Root = new() { Id = StableId(RootId), Name = RootId, PartBy = PartByRange.Day };

    /// <summary>
    /// A process-independent 32-bit id (FNV-1a over the UTF-16 code units).
    /// </summary>
    /// <remarks>
    /// <c>string.GetHashCode()</c> is randomised per process on .NET, so an id taken from it means
    /// something different after every restart - fine as long as it never leaves the process, a trap
    /// the moment it is compared against, or stored, a value that came from another process. The
    /// only variant name that reaches storage is <c>Name</c>; the id stays in memory, and a
    /// stable one makes that true by construction rather than by convention.
    /// </remarks>
    internal static int StableId(string name)
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
