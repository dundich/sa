namespace Sa.Configuration.SecretStore;

public sealed class SecretOptions
{
    public string FileName { get; set; } = Secrets.DefaultFileName;
    public string[] Args { get; set; } = Environment.GetCommandLineArgs();
    public string EnvironmentName { get; set; } = Secrets.GetEnvironmentName();
}
