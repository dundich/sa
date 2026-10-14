using Sa.Partitional.PostgreSql.Classes;
using System.Text;

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

        if (!settings.DefaultPartition)
        {
            string[] parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                parts[i] = settings.CreateNestedSql(values[..(i + 1)]);

            return string.Join(Environment.NewLine, parts);
        }

        // With DEFAULT partitions every level carries its own catch-all, so each new sibling has to
        // drag the rows it owns out of the parent's default before it can be created.
        var sql = new StringBuilder();

        for (int i = 0; i < values.Length; i++)
        {
            ReadOnlySpan<StrOrNum> parent = values[..i];
            ReadOnlySpan<StrOrNum> child = values[..(i + 1)];
            string temp = $"_sa_move_l{i}";

            sql.Append(SqlTemplate.MoveRowsOutOfDefaultSql(
                settings.GetQualifiedDefaultTableName(parent),
                settings.ListPartPredicate(i, values[i]),
                temp));

            sql.Append(settings.CreateNestedSql(child));

            // The child's own default must exist before the staged rows are routed into it.
            sql.Append(settings.CreateDefaultPartSql(child));

            sql.Append(SqlTemplate.MoveRowsBackSql(settings.GetQualifiedTableName(child), temp));
        }

        return sql.ToString();
    }
}
