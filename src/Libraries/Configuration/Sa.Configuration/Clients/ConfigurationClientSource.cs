using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.Clients;

/// <summary>
/// Registers clients that live in a configuration section — the no-database provider.
/// </summary>
public static class ConfigurationClientSetup
{
    /// <summary>
    /// Registers a provider that reads the section: its children are the clients, and each
    /// client's own section supplies rows with the same key names as the core configuration.
    /// </summary>
    /// <remarks>
    /// The section is re-read on every <see cref="IClientConfigurations.ReloadAsync"/>, so a client
    /// added to the section appears without a restart and existing values are refreshed.
    /// <code>
    /// builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    ///     clients.UseConfiguration(builder.Configuration));
    /// </code>
    /// </remarks>
    /// <param name="registration">The client registration being configured.</param>
    /// <param name="configuration">Configuration the section is read from.</param>
    /// <param name="options">Section layout; the defaults read the <c>clients</c> section.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static SaClientConfigurationRegistration UseConfiguration(
        this SaClientConfigurationRegistration registration,
        IConfiguration configuration,
        ConfigurationClientSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(configuration);

        options ??= new ConfigurationClientSourceOptions();
        options.Validate();

        // The section lives in the same configuration the clients read their core values from, so
        // its subtree has to be hidden from the views — otherwise one client would read another
        // client's values through the core fallback.
        registration.HiddenSectionPath = options.SectionPath;

        return registration.AddLoader(ConfigurationClientRows.Loader(configuration, options));
    }
}

/// <summary>
/// Turns a configuration section into the flat rows the registry consumes.
/// </summary>
internal static class ConfigurationClientRows
{
    public static RowLoader Loader(IConfiguration configuration, ConfigurationClientSourceOptions options)
        => (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyList<ClientSetting>>(Read(configuration, options));
        };

    private static List<ClientSetting> Read(
        IConfiguration configuration,
        ConfigurationClientSourceOptions options)
    {
        List<ClientSetting> rows = [];

        foreach (IConfigurationSection client in configuration.GetSection(options.SectionPath).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(client.Key))
                continue;

            Collect(client, client.Key, string.Empty, rows);
        }

        return rows;
    }

    private static void Collect(
        IConfigurationSection section,
        string clientId,
        string path,
        List<ClientSetting> rows)
    {
        if (section.Value is not null && path.Length > 0)
            rows.Add(new ClientSetting(clientId, path, section.Value));

        foreach (IConfigurationSection child in section.GetChildren())
        {
            Collect(
                child,
                clientId,
                path.Length == 0 ? child.Key : $"{path}{ConfigurationPath.KeyDelimiter}{child.Key}",
                rows);
        }
    }
}
