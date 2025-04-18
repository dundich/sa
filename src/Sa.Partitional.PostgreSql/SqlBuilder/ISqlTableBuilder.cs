using Sa.Classes;

namespace Sa.Partitional.PostgreSql;

public interface ISqlTableBuilder
{
    string FullName { get; }

    ITableSettings Settings { get; }

    string SelectPartsFromDate { get; }
    string SelectPartsToDate { get; }

    string GetPartsSql(StrOrNum[] partValues);

    string CreateSql(DateTimeOffset date, params StrOrNum[] partValues);
}
