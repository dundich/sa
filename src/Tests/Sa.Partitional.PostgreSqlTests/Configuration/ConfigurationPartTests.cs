using Sa.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Configuration;




public class ConfigurationPartTests(ConfigurationPartTests.Fixture fixture) : IClassFixture<ConfigurationPartTests.Fixture>
{
    public class Fixture : SaSubFixture<ISettingsBuilder>
    {
        public Fixture()
        {
            Services.AddPartitional((_, builder) => builder.AddSchema(schema =>
            {

                schema
                    .AddTable("test_1",
                        "part TEXT NOT NULL",
                        "lock_instance_id TEXT NOT NULL",
                        "lock_expires_on bigint NOT NULL",
                        "payload_id TEXT NOT NULL"
                    )
                    .PartByList("part", "lock_instance_id")
                    .TimestampAs("date")
                    ;

                schema
                    .AddTable("test_2",
                        "name TEXT NOT NULL"
                    )
                    .PartByList("name")
                    ;

            }));

            Services.AddPartitional((_, builder) => builder.AddSchema(schema =>
            {
                schema
                    .AddTable("test_3",
                        "text TEXT NOT NULL"
                    )
                ;
            }));
        }
    }

    [Fact]
    public void PartitionalPostgreSql_SettingsBuiling()
    {
        ISettingsBuilder builder = fixture.Sub;
        ITableSettingsStorage settings = builder.Build();
        Assert.Equal(3, settings.Tables.Count);
    }

    [Fact]
    public void PartitionalPostgreSql_CheckIdName_Test()
    {
        ISettingsBuilder builder = fixture.Sub;
        ITableSettingsStorage storage = builder.Build();
        ITableSettings? settings = storage.Tables.FirstOrDefault(c => c.FullName == "public.test_1");
        Assert.NotNull(settings);
        Assert.NotNull(builder);
        Assert.Equal("part", settings.IdFieldName);
    }
}
