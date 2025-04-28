namespace Sa.Configuration;

public class SaEnvironment
{
    public string DefaultFileSecrets { get; set; } = "secrets.txt";
    public string SA_HOST_KEY { get; set; } = "sa_host_key";

    public string EnvironmentName { get; set; } =
    (
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("environment")
        ?? "Production"
    );

    public static SaEnvironment Default { get; set; } = new();
}
