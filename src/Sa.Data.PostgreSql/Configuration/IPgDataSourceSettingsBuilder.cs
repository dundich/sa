namespace Sa.Data.PostgreSql;

public interface IPgDataSourceSettingsBuilder
{
    void WithConnectionString(string connectionString);
    void WithConnectionString(Func<IServiceProvider, string> implementationFactory);
}
