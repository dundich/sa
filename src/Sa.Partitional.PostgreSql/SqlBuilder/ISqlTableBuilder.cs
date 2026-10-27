using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

internal interface ISqlTableBuilder
{
    string FullName { get; }

    ITableSettings Settings { get; }

    /// <summary>
    /// The range strategy of the table, hoisted out of <see cref="Settings"/> so a caller that only
    /// needs the granularity (the partition cache sizes its snapshot window by it) does not have to
    /// pull in the whole settings object.
    /// </summary>
    PgPartBy PartBy { get; }

    string SelectPartsFromDate { get; }
    string SelectPartsToDate { get; }

    string GetPartsSql(StrOrNum[] partValues);

    string CreateSql(DateTimeOffset date, params StrOrNum[] partValues);
}
