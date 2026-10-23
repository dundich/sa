using Sa.Configuration.SecretStore.Stores;

namespace Sa.ConfigurationTests;

public class DotEnvSecretStoreTests
{
    [Fact]
    public void Ctor_ParsesSupportedSyntax()
    {
        using TempDirectory directory = new();
        string path = directory.Write(
            ".env",
            """
            # comment
            A=1
            export B=two words
            C="quoted value"
            D='single value'
            E=`backtick value`
            F="line1\nline2"
            G = spaced value
            H=first
            H=second
            malformed line without separator
            """);

        var store = new DotEnvSecretStore(path);

        Assert.Equal("1", store.GetSecret("A"));
        Assert.Equal("two words", store.GetSecret("B"));
        Assert.Equal("quoted value", store.GetSecret("C"));
        Assert.Equal("single value", store.GetSecret("D"));
        Assert.Equal("backtick value", store.GetSecret("E"));
        Assert.Equal("line1\nline2", store.GetSecret("F"));
        Assert.Equal("spaced value", store.GetSecret("G"));
        Assert.Equal("second", store.GetSecret("H"));
        Assert.Null(store.GetSecret("malformed line without separator"));
    }

    [Fact]
    public void Ctor_MissingFile_ReturnsEmptyStore()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "missing.env");

        var store = new DotEnvSecretStore(path);

        Assert.Null(store.GetSecret("any"));
    }

    [Fact]
    public void Ctor_HandlesBomAndCrLf()
    {
        using TempDirectory directory = new();
        string path = directory.Write(".env", "\uFEFFA=1\r\nB=2\r\n");

        var store = new DotEnvSecretStore(path);

        Assert.Equal("1", store.GetSecret("A"));
        Assert.Equal("2", store.GetSecret("B"));
    }

    [Fact]
    public void Ctor_DoubleQuotedEscapes_AreDecoded()
    {
        using TempDirectory directory = new();
        string path = directory.Write(".env", "A=\"tab\\there\"\nB=\"quote\\\"inside\"\nC=\"back\\\\slash\"\nD=\"C:\\\\Users\\\\name\"\n");

        var store = new DotEnvSecretStore(path);

        Assert.Equal("tab\there", store.GetSecret("A"));
        Assert.Equal("quote\"inside", store.GetSecret("B"));
        Assert.Equal("back\\slash", store.GetSecret("C"));
        Assert.Equal("C:\\Users\\name", store.GetSecret("D"));
    }
}
