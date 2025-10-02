using Sa.Classes;

namespace Sa.Partitional.PostgreSql;

public interface ISqlBuilder
{
    ISqlTableBuilder? this[string tableName] { get; }

    IReadOnlyCollection<ISqlTableBuilder> Tables { get; }

    IAsyncEnumerable<string> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve);

    string CreatePartSql(string tableName, DateTimeOffset date, StrOrNum[] partValues);

    string SelectPartsQualifiedTablesSql(string tableName, StrOrNum[] partValues);

    string SelectPartsQualifiedTablesSql(string qualifiedTablesSql);

    string SelectPartsFromDateSql(string tableName);

    string SelectPartsToDateSql(string tableName);
}
