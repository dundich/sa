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

            if (!inQuotes && FieldSeparators.Contains(c)) return field[..i].Trim('"');
        }

        return field.Trim('"');
    }

    /// <summary>
    /// Reports whether <paramref name="columnName"/> is declared among <paramref name="fields"/>.
    /// </summary>
    /// <param name="fields">The raw field definitions of a table.</param>
    /// <param name="columnName">The column name to look for, compared exactly.</param>
    /// <returns><see langword="true"/> when one of the fields declares that column.</returns>
    public static bool HasColumn(string[] fields, string columnName)
    {
        foreach (string field in fields)
        {
            if (Extract(field).SequenceEqual(columnName)) return true;
        }

        return false;
    }
}
