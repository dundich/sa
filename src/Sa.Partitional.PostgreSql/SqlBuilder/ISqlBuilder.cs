using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

internal interface ISqlBuilder
{
    ISqlTableBuilder? this[string tableName] { get; }

    IReadOnlyCollection<ISqlTableBuilder> Tables { get; }

    IAsyncEnumerable<string> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve);
    string SelectPartsFromDateSql(string tableName);

    string SelectPartsToDateSql(string tableName);
}
