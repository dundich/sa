using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

/// <summary>
/// public."_outbox_root__y2024m12d11"
/// </summary>
internal sealed class SqlPartRangeBuilder(ITableSettings settings)
{
    public string CreateSql(DateTimeOffset date, StrOrNum[] partValues)
    {
        if (!settings.DefaultPartition)
            return settings.CreatePartByRangeSql(date, partValues);

        // The deepest parent's DEFAULT holds rows whose date fell outside every created range; the
        // leaf about to be created owns some of them and has to take them over.
        const string temp = "_sa_move_r";

        return SqlTemplate.MoveRowsOutOfDefaultSql(
                   settings.GetQualifiedDefaultTableName(partValues),
                   settings.RangePartPredicate(date),
                   temp)
             + settings.CreatePartByRangeSql(date, partValues)
             + SqlTemplate.MoveRowsBackSql(settings.GetQualifiedTableName(date, partValues), temp);
    }
}
