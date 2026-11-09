using Sa.Partitional.PostgreSql.Configuration;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// The default schema is derived from the connection's <c>Search Path</c>, which is a
/// comma-separated list of (possibly quoted) identifiers. Handing that string to a table
/// declaration as if it were one schema name is what these tests pin.
/// </summary>
public class SearchPathSchemaTests
{
    [Theory]
    [InlineData(null, "public")]
    [InlineData("", "public")]
    [InlineData("   ", "public")]
    [InlineData("public", "public")]
    [InlineData("tenant1", "tenant1")]
    // The first entry is what PostgreSQL resolves an unqualified name to.
    [InlineData("tenant1, public", "tenant1")]
    [InlineData(" tenant1 , public", "tenant1")]
    // An unquoted identifier is folded to lower case by PostgreSQL itself.
    [InlineData("MySchema", "myschema")]
    // A quoted one keeps its case exactly, and doubled quotes are the escape.
    [InlineData("\"MySchema\"", "MySchema")]
    [InlineData("\"My Schema\"", "My Schema")]
    [InlineData("\"a\"\"b\"", "a\"b")]
    // $user is the role's schema and the other two are implicit system schemas.
    [InlineData("$user", "public")]
    [InlineData("$user, tenant", "tenant")]
    [InlineData("pg_catalog, tenant", "tenant")]
    [InlineData("pg_temp, \"$user\", public", "public")]
    [InlineData(",", "public")]
    public void Resolve_ReturnsTheEffectiveFirstSchema(string? searchPath, string expected)
        => Assert.Equal(expected, SearchPathSchema.Resolve(searchPath));
}
