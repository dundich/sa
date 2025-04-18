﻿using Sa.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

/// <summary>
/// public."_outbox_root__y2024m12d11"
/// </summary>
internal class SqlPartRangeBuilder(ITableSettings settings)
{
    public string CreateSql(DateTimeOffset date, StrOrNum[] partValues)
        => settings.CreatePartByRangeSql(date, partValues);
}
