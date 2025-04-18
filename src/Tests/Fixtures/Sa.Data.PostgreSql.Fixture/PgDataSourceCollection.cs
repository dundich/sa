namespace Sa.Data.PostgreSql.Fixture;

/// <summary>
/// <see cref="https://blog.jetbrains.com/dotnet/2023/10/24/how-to-use-testcontainers-with-dotnet-unit-tests/#container-per-collection-strategy"/>
/// </summary>
[CollectionDefinition(nameof(PgDataSourceFixture))]
public class PgDataSourceCollection : ICollectionFixture<PgDataSourceFixture>;
