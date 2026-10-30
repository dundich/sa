using System.Buffers;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

/// <summary>
/// Reads the column name out of a raw field definition - the first token of
/// <c>"my id" INT NOT NULL</c> is <c>"my id"</c>, not <c>"my</c>.
/// </summary>
/// <remarks>
/// Cutting on the first whitespace and trimming the quotes afterwards is wrong for a quoted
/// identifier that contains a space, and it treats a field whose name merely <i>starts with</i> the
/// column being looked for (<c>created_at_idx</c> for <c>created_at</c>) as a match. Both mistakes
/// end up in the generated DDL: a duplicated column, or a primary key and a partition key pointing
/// at a column that was never declared.
/// </remarks>
internal static class SqlFieldName
{
    /// <summary>
    /// Whitespace that separates a column name from the rest of its definition.
    /// </summary>
    private static readonly SearchValues<char> FieldSeparators = SearchValues.Create(" \t\r\n");

    /// <summary>
    /// Extracts the column name from a field definition, returning it unquoted.
    /// </summary>
    /// <param name="field">A raw field definition such as <c>"id" INT NOT NULL</c>.</param>
    /// <returns>The declared column name, or an empty span when there is none.</returns>
    public static ReadOnlySpan<char> Extract(ReadOnlySpan<char> field)
    {
        field = field.Trim();

        int end = NameEnd(field);

        return (end < 0 ? field : field[..end]).Trim('"');
    }

    /// <summary>
    /// Extracts the declared type token of a field definition - <c>bigint</c> for
    /// <c>"created_at" bigint NOT NULL</c>.
    /// </summary>
    /// <param name="field">A raw field definition.</param>
    /// <returns>
    /// The first token after the column name, or an empty span when the definition only names the
    /// column.
    /// </returns>
    public static ReadOnlySpan<char> ExtractType(ReadOnlySpan<char> field)
    {
        field = field.Trim();

        int end = NameEnd(field);

        if (end < 0 || end + 1 >= field.Length) return default;

        ReadOnlySpan<char> rest = field[(end + 1)..].TrimStart();

        int firstSeparator = rest.IndexOfAny(FieldSeparators);

        return firstSeparator < 0 ? rest : rest[..firstSeparator];
    }

    /// <summary>
    /// Index of the whitespace that ends the column name, or <c>-1</c> when the definition is just
    /// a name. Quote-aware, so a space inside <c>"my id"</c> is part of the name.
    /// </summary>
    private static int NameEnd(ReadOnlySpan<char> field)
    {
        bool inQuotes = false;

        for (int i = 0; i < field.Length; i++)
        {
            char c = field[i];

            if (c == '"')
            {
                // A doubled quote inside a quoted name is an escaped quote, not the closing one.
                if (inQuotes && i + 1 < field.Length && field[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && FieldSeparators.Contains(c)) return i;
        }

        return -1;
    }

    /// <summary>
    /// Reports whether <paramref name="columnName"/> is declared among <paramref name="fields"/>.
    /// </summary>
    /// <param name="fields">The raw field definitions of a table.</param>
    /// <param name="columnName">The column name to look for, compared exactly.</param>
    /// <returns><see langword="true"/> when one of the fields declares that column.</returns>
    public static bool HasColumn(IReadOnlyList<string> fields, string columnName)
    {
        foreach (string field in fields)
        {
            if (Extract(field).SequenceEqual(columnName)) return true;
        }

        return false;
    }
}
