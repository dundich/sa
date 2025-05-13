using System.Text.RegularExpressions;

namespace Sa.Configuration.SecretStore.Engine;

internal partial class SecretService
{
    [GeneratedRegex(@"\{\{(\w+)\}\}", RegexOptions.None | RegexOptions.Compiled)]
    internal static partial Regex PlaceholderRegex();
}
