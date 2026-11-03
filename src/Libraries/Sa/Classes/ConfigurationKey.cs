using Microsoft.Extensions.Configuration;

namespace Sa.Classes;

/// <summary>
/// Normalizes keys coming from a storage that uses the environment-variable spelling of a
/// configuration path. A double underscore is accepted as an alias for the
/// <see cref="ConfigurationPath.KeyDelimiter"/> separator, exactly as
/// <c>AddEnvironmentVariables()</c> does, so <c>sa__pg__host</c> and <c>sa:pg:host</c> address
/// the same setting.
/// </summary>
/// <remarks>
/// <see cref="string.Replace(string, string, StringComparison)"/> walks left to right, so a run of
/// four underscores becomes two delimiters (<c>a____b</c> → <c>a::b</c>) — the same edge case
/// the environment-variable provider has. The result is idempotent for already-normalized keys.
/// </remarks>
internal static class ConfigurationKey
{
    private const string EnvironmentSeparator = "__";

    /// <summary>
    /// Returns <paramref name="key"/> with every <c>__</c> replaced by
    /// <see cref="ConfigurationPath.KeyDelimiter"/>. Returns the same instance when there is
    /// nothing to replace.
    /// </summary>
    internal static string Normalize(string key)
        => key.Contains(EnvironmentSeparator, StringComparison.Ordinal)
            ? key.Replace(EnvironmentSeparator, ConfigurationPath.KeyDelimiter, StringComparison.Ordinal)
            : key;
}
