using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

public interface ITableBuilder
{
    ITableBuilder AddFields(params string[] sqlFields);
    ITableBuilder PartByList(params string[] fieldNames);
    ITableBuilder PartByRange(PgPartBy partBy, string? timestampFieldName = null);

    ITableBuilder TimestampAs(string timestampFieldName);

    ITableBuilder WithPartSeparator(string partSeparator);
    ITableBuilder WithFillFactor(int fillFactor);

    ITableBuilder WithPartTablePostfix(string postfix);

    ITableBuilder AddPostSql(Func<string> postSql);
    ITableBuilder AddConstraintPkSql(Func<string> pkSql);


    ITableSettings Build();

    ITableBuilder AddMigration(IPartTableMigrationSupport migrationSupport);

    ITableBuilder AddMigration(Func<CancellationToken, Task<StrOrNum[][]>> getPartValues);

    ITableBuilder AddMigration(params StrOrNum[] partValues);

    ITableBuilder AddMigration(StrOrNum parent, StrOrNum[] childs)
    {
        foreach (StrOrNum child in childs) AddMigration(parent, child);
        return this;
    }
}
