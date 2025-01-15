using Sa.Classes;
using Sa.Extensions;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

/// <summary>
/// public."_outbox_root"
/// </summary>
internal class SqlPartListBuilder(ITableSettings settings)
{
    public string CreateSql(StrOrNum[] partValues)
        => partValues.Length > 0
            ? partValues
                .Select((c, i) => settings.CreateNestedSql(partValues[0..(i + 1)]))
                .JoinByString(Environment.NewLine)
            : string.Empty
            ;
}
