using System.Globalization;
using System.Text.RegularExpressions;

namespace Sa.Configuration.CommandLine;


/// <summary>
/// Provides a robust way to parse command-line arguments.
/// </summary>
/// <remarks>
/// This class handles various parameter formats and provides helper methods for different data types.
/// Usage example:
/// <code>
///     /Sa.exe
///         --config_db /opt/foo/config_db.json
///         --config_file /opt/foo/appsettings.json
///         --config_nlog /opt/foo/NLog.config
///         -ip_override 127.0.0.1
/// </code>
/// <seealso href="https://www.codeproject.com/Articles/3111/C-NET-Command-Line-Arguments-Parser"/>
/// </remarks>
public sealed partial class Arguments
{
    private static readonly Regex s_ReSpliter = SpliterRegex();
    private static readonly Regex s_ReRemover = RemoverRegex();
    private readonly Dictionary<string, string?> _parameters = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="Arguments"/> class.
    /// </summary>
    /// <param name="args">The command-line arguments to parse.</param>
    public Arguments(params string[] args)
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

    /// <summary>
    /// Creates a new instance of the <see cref="Arguments"/> class with the default command-line arguments.
    /// </summary>
    /// <param name="args">Optional command-line arguments. If null, Environment.GetCommandLineArgs() is used.</param>
    /// <returns>A new instance of the Arguments class.</returns>
    public static Arguments CreateDefault(string[]? args = null) => new(args ?? Environment.GetCommandLineArgs());

    /// <summary>
    /// Gets the value associated with the specified parameter name.
    /// </summary>
    /// <param name="param">The parameter name to retrieve.</param>
    /// <returns>The value associated with the parameter, or null if not found.</returns>
    public string? this[string param] => _parameters.TryGetValue(param, out var value) ? value : null;

    /// <summary>
    /// Gets all parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters => _parameters;

    /// <summary>
    /// Splits command-line arguments into parameter-value pairs.
    /// </summary>
    /// <param name="parts">The command-line arguments to split.</param>
    /// <returns>A list of parameter-value pairs.</returns>
    internal static IReadOnlyList<string> SplitToPairs(IReadOnlyList<string> parts)
    {
        List<string> result = [];
        string? currentParam = null;

        foreach (var part in parts)
        {
            if (IsFlag(part))
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

    /// <summary>
    /// Determines whether the token is a flag (parameter name) rather than a value.
    /// A token that starts with one or two dashes followed immediately by a digit
    /// (e.g. <c>-1</c>, <c>--1</c>, <c>-1.5</c>) is treated as a value, not a flag,
    /// so that negative numbers can be passed as parameter values.
    /// </summary>
    private static bool IsFlag(string part)
    {
        if (!part.StartsWith('-'))
            return false;

        int index = 0;
        while (index < part.Length && part[index] == '-')
        {
            index++;
        }

        // More than two leading dashes is not a flag.
        if (index > 2)
            return false;

        // Dashes immediately followed by a digit (or '-'/'.' then a digit) mean a negative number value.
        if (index < part.Length)
        {
            char next = part[index];
            if (char.IsDigit(next))
                return false;
            if ((next == '-' || next == '.') && index + 1 < part.Length && char.IsDigit(part[index + 1]))
                return false;
        }

        return true;
    }

    private static void Add(List<string> result, string? currentParam)
    {
        if (currentParam == null) return;
        result.Add(currentParam.Contains('=') ? currentParam : $"{currentParam}=true");
    }

    /// <summary>
    /// Checks if a parameter exists.
    /// </summary>
    /// <param name="param">The parameter name to check.</param>
    /// <returns>true if the parameter exists; otherwise false.</returns>
    public bool Contains(string param)
    {
        return _parameters.ContainsKey(param);
    }

    /// <summary>
    /// Gets a parameter value as a boolean.
    /// </summary>
    /// <param name="param">The parameter name.</param>
    /// <returns>
    /// <see langword="null"/> if the parameter is absent;
    /// <see langword="true"/> if its value is truthy ("true"/"1"/"yes"/"on");
    /// <see langword="false"/> if present but not truthy.
    /// </returns>
    public bool? GetBool(string param)
    {
        if (!Contains(param)) return default;

        var value = _parameters[param]?.ToLowerInvariant();
        return value == "true" || value == "1" || value == "yes" || value == "on";
    }

    /// <summary>
    /// Gets a parameter value as an integer, defaulting to null if not found or invalid.
    /// </summary>
    /// <param name="param">The parameter name.</param>
    /// <returns>The integer value if found and valid; otherwise null.</returns>
    public int? GetInt(string param)
    {
        if (!Contains(param) || !int.TryParse(_parameters[param], CultureInfo.InvariantCulture, out int result))
            return default;

        return result;
    }

    /// <summary>
    /// Gets a parameter value as a float, defaulting null.
    /// </summary>
    /// <param name="param">The parameter name.</param>
    /// <returns>The float value if found and valid; otherwise null.</returns>
    public float? GetFloat(string param)
    {
        if (!Contains(param)
            || !float.TryParse(_parameters[param], CultureInfo.InvariantCulture, out float result))
            return default;

        return result;
    }

    /// <summary>
    /// Gets a parameter value as a long, defaulting to null if not found or invalid.
    /// </summary>
    /// <param name="param">The parameter name.</param>
    /// <returns>The long value if found and valid; otherwise null.</returns>
    public long? GetLong(string param)
    {
        if (!Contains(param)
            || !long.TryParse(_parameters[param], CultureInfo.InvariantCulture, out long result))
            return default;

        return result;
    }

    /// <summary>
    /// Gets a parameter value as a TimeSpan, defaulting to null if not found or invalid.
    /// </summary>
    /// <param name="param">The parameter name.</param>
    /// <returns>The TimeSpan value if found and valid; otherwise null.</returns>
    public TimeSpan? GetTimeSpan(string param)
    {
        if (!Contains(param)
            || !TimeSpan.TryParse(_parameters[param], CultureInfo.InvariantCulture, out TimeSpan result))
            return default;

        return result;
    }
}
