namespace Sa.Configuration.Clients;

/// <summary>
/// Layout of the section that holds per-client settings in a static configuration source
/// (appsettings.json, environment variables, command-line arguments) — the no-database
/// counterpart of a client settings table.
/// </summary>
/// <remarks>
/// With the defaults, <c>clients:acme:s3_url</c> is <c>acme</c>'s row for the <c>s3_url</c> setting,
/// and <c>s3_url</c> outside the section is the shared core value. The client id is the child key
/// under <see cref="SectionPath"/>; the keys inside a client's section use the same names as the
/// core configuration and are read without any prefix.
/// </remarks>
public sealed record ConfigurationClientSourceOptions
{
    /// <summary>
    /// Path of the section whose children are the clients. Defaults to <c>clients</c>.
    /// </summary>
    public string SectionPath { get; init; } = "clients";

    internal void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(SectionPath);
}
