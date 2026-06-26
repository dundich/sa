using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

internal interface ISqlTableBuilder
{
    string FullName { get; }

    ITableSettings Settings { get; }

    string SelectPartsFromDate { get; }
    string SelectPartsToDate { get; }

    string GetPartsSql(StrOrNum[] partValues);

    string CreateSql(DateTimeOffset date, params StrOrNum[] partValues);

    /// <summary>
    /// Creates SQL for a table with no LIST partitioning (RANGE only).
    /// </summary>
    string CreateSql(DateTimeOffset date);
}
