using Sa.Classes;

namespace Sa.Partitional.PostgreSql;

internal interface ISqlTableBuilder
{
    string FullName { get; }

    ITableSettings Settings { get; }

    string SelectPartsFromDate { get; }
    string SelectPartsToDate { get; }

    string GetPartsSql(StrOrNum[] partValues);

    string CreateSql(DateTimeOffset date, params StrOrNum[] partValues);
}
