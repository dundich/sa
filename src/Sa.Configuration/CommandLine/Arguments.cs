using System.Text.RegularExpressions;

namespace Sa.Configuration.CommandLine;


/// <summary>
/// <code>
///     /Sa.exe
///         --config_db /opt/service_configs/config_db.json
///         --config_file /opt/service_configs/appsettings.json
///         --config_nlog /opt/service_configs/NLog.config
///         -ip_override $(hostname -i)
///
/// </code>
/// <seealso href="https://www.codeproject.com/Articles/3111/C-NET-Command-Line-Arguments-Parser"/>
/// </summary>
public partial class Arguments
{
    private static readonly Regex s_ReSpliter = SpliterRegex();
    private static readonly Regex s_ReRemover = RemoverRegex();
    private readonly Dictionary<string, string> _parameters = [];

    public Arguments(params IReadOnlyList<string> args)
    {
        var pairs = SplitToPairs(args);

        foreach (string arg in pairs)
        {
            // Split the argument into parts
            var parts = s_ReSpliter.Split(arg, 3);

            if (parts.Length == 3)
            {
                var currentParameter = parts[1];
                var cleanedValue = s_ReRemover.Replace(parts[2], "$1");
                _parameters[currentParameter] = cleanedValue;
            }
        }
    }

    public string? this[string param] => _parameters.TryGetValue(param, out var value) ? value : null;


    public static IReadOnlyList<string> SplitToPairs(IReadOnlyList<string> parts)
    {
        var result = new List<string>();
        string? currentParam = null;

        foreach (var part in parts)
        {
            if (part.StartsWith('-'))
            {
                // Add the previous parameter to the result if it exists
                Add(result, currentParam);
                currentParam = part; // Start a new parameter
            }
            else if (currentParam != null)
            {
                // Append value to the current parameter
                currentParam = currentParam.Contains('=')
                    ? $"{currentParam} {part}"
                    : $"{currentParam}={part}";
            }
        }

        // Add the last parameter if it exists
        Add(result, currentParam);

        return result;
    }

    private static void Add(List<string> result, string? currentParam)
    {
        if (currentParam == null) return;
        result.Add(currentParam.Contains('=') ? currentParam : $"{currentParam}=true");
    }
}

