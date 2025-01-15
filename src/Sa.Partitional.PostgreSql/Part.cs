using Sa.Classes;

namespace Sa.Partitional.PostgreSql;

public record Part(string Name, PartByRange PartBy) : Enumeration<Part>(Name.GetHashCode(), Name)
{
    public const string RootId = "root";

    public static readonly Part Root = new(RootId, PartByRange.Day);
}
