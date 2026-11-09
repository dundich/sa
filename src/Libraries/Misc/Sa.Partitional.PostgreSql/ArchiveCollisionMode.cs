namespace Sa.Partitional.PostgreSql;

/// <summary>
/// What the archive pass does when the archive schema already holds a table with the name the
/// expired partition would get.
/// </summary>
/// <remarks>
/// Archived tables keep their name, so the clash is reachable: a window that was archived, written
/// to again and expired a second time ends up live while its earlier copy still sits in the archive
/// schema. <c>SET SCHEMA</c> would fail on that name.
/// </remarks>
public enum ArchiveCollisionMode
{
    /// <summary>
    /// Leave the live partition where it is, log the clash (<c>EventId 410</c>) and retry on the next
    /// pass. Nothing is renamed or dropped, and the partition never leaves its tree. This is the
    /// default: renaming data that an operator may have archived for compliance is not something the
    /// library does on its own.
    /// </summary>
    Fail = 0,

    /// <summary>
    /// Rename the existing archived copy to <c>{name}{separator}prev$1</c> (then <c>$2</c>, …), so the
    /// canonical name always holds the most recent data and the older copy is preserved under a
    /// derived name. The live partition is then archived as usual.
    /// </summary>
    RenameExisting = 1,
}
