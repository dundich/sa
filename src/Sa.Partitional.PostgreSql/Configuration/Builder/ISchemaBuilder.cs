namespace Sa.Partitional.PostgreSql;

public interface ISchemaBuilder
{
    ITableBuilder CreateTable(string tableName);
    ITableBuilder AddTable(string tableName, params string[] sqlFields);
    ITableSettings[] Build();
}
