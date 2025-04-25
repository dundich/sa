using System.Text.RegularExpressions;

namespace Sa.Configuration.CommandLine;

public partial class Arguments
{
    [GeneratedRegex(@"^-{1,2}|=", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    internal static partial Regex SpliterRegex();

    [GeneratedRegex(@"^['""]?(.*?)['""]?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    internal static partial Regex RemoverRegex();
}
