namespace Sa.Partitional.PostgreSql.Configuration;

/// <summary>
/// Resolves the schema a table declaration defaults to from a PostgreSQL <c>Search Path</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sa.Data.PostgreSql.IPgDataSource.GetSearchPath"/> returns the connection string's
/// <c>Search Path</c> verbatim, which is a comma-separated list of identifiers - and it may be
/// absent entirely. Used as-is it becomes a single (wrong) schema name: <c>"tenant1, public"</c>
/// instead of <c>tenant1</c>.
/// </para>
/// <para>
/// The first entry that can actually hold application tables wins, because that is what PostgreSQL
/// itself resolves an unqualified name to. Unquoted entries are folded to lower case (PostgreSQL's
/// identifier folding), quoted entries keep their case exactly. <c>$user</c>, <c>pg_catalog</c> and
/// <c>pg_temp</c> are skipped: the first resolves to the connected role's schema and the other two
/// are implicit system schemas, none of which is a place for a partitioned application table.
/// </para>
/// <para>
/// Same rule as <c>PostgresFileStorageSchema</c> in <c>Sa.HybridFileStorage.Postgres</c>; kept
/// local because that package is the other side of the dependency graph.
/// </para>
/// </remarks>
internal static class SearchPathSchema
{
    /// <summary>The schema used when the search path names no usable entry.</summary>
    public const string Fallback = "public";

    /// <summary>
    /// Returns the schema an unqualified table declaration resolves to, or <see cref="Fallback"/>.
    /// </summary>
    /// <param name="searchPath">The raw <c>Search Path</c> value (comma-separated, possibly empty).</param>
    public static string Resolve(string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(searchPath)) return Fallback;

        foreach (string rawEntry in searchPath.Split(','))
        {
            string entry = rawEntry.Trim();

            if (entry.Length == 0) continue;

            // A quoted entry is already case-exact, so only its quotes (and their escapes) are
            // removed. Anything else goes through PostgreSQL's folding.
            string candidate = entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"'
                ? entry[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
                : entry.ToLowerInvariant();

            if (candidate.Length == 0) continue;

            if (candidate is "$user" or "pg_catalog" or "pg_temp") continue;

            return candidate;
        }

        return Fallback;
    }
}
