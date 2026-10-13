using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

/// <summary>
/// public."_outbox_root"
/// </summary>
internal sealed class SqlPartListBuilder(ITableSettings settings)
{
    public string CreateSql(StrOrNum[] partValues)
    {
        if (partValues.Length == 0)
            return string.Empty;

        // Each nested level is built from a growing prefix <i>view</i> of the caller's array —
        // `values[..(i + 1)]` is a zero-copy span slice, so no per-level `StrOrNum[]` copy. The
        // resulting SQL strings are collected into one buffer that `string.Join` consumes.
        ReadOnlySpan<StrOrNum> values = partValues;
        string[] parts = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
            parts[i] = settings.CreateNestedSql(values[..(i + 1)]);

        return string.Join(Environment.NewLine, parts);
    }
}
