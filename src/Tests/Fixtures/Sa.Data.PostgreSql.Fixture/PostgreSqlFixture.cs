using Sa.Fixture;
using Testcontainers.PostgreSql;

namespace Sa.Data.PostgreSql.Fixture;


/// <summary>
/// <seealso href="https://blog.jetbrains.com/dotnet/2023/10/24/how-to-use-testcontainers-with-dotnet-unit-tests/"/>
/// </summary>
public abstract class PostgreSqlFixture<TSub, TSettings> : SaFixture<TSub, TSettings>
    where TSettings : PostgreSqlFixtureSettings
    where TSub : notnull
{
    private readonly PostgreSqlContainer container;

    protected PostgreSqlFixture(TSettings settings)
        : base(settings)
    {
        var builder = new PostgreSqlBuilder()
            .WithImage(settings.DockerImage)
            ;

        settings.Configure?.Invoke(builder);
        container = builder.Build();
    }

    public virtual string ConnectionString => container.GetConnectionString();

    public string ContainerId => $"{container.Id}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await container.StartAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
        await base.DisposeAsync();
    }
}
