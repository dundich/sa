using System.Text.RegularExpressions;

namespace Sa.Configuration.CommandLine;


/// <summary>
/// https://www.codeproject.com/Articles/3111/C-NET-Command-Line-Arguments-Parser
/// <code>
///     /Sa.exe
///         --config_db /opt/service_configs/config_db.json
///         --config_file /opt/service_configs/appsettings.json
///         --config_nlog /opt/service_configs/NLog.config
///         -ip_override $(hostname -i)
///
/// </code>
/// </summary>
public partial class Arguments
{
    private static readonly Regex s_ReSpliter = SpliterRegex();
    private static readonly Regex s_ReRemover = RemoverRegex();
    private readonly Dictionary<string, string> _parameters = [];

    public Arguments(params IReadOnlyList<string> args)
    {
        var pairs = SplitToPairs(args);

        string? currentParameter = null;

        foreach (string arg in pairs)
        {
            // Разделяем аргумент на части
            var parts = s_ReSpliter.Split(arg, 3);

            switch (parts.Length)
            {
                case 3:
                    // Если это параметр с значением
                    if (currentParameter != null && !_parameters.ContainsKey(currentParameter))
                    {
                        _parameters[currentParameter] = "true"; // Устанавливаем значение по умолчанию
                    }
                    currentParameter = parts[1]; // Устанавливаем новый текущий параметр
                    _parameters[currentParameter] = s_ReRemover.Replace(parts[2], "$1"); // Добавляем значение
                    currentParameter = null; // Сбрасываем текущий параметр
                    break;
            }
        }

        // Если остался текущий параметр без значения, устанавливаем его в true
        if (currentParameter != null && !_parameters.ContainsKey(currentParameter))
        {
            _parameters[currentParameter] = "true";
        }
    }

    // Получение значения параметра
    public string? this[string param] => _parameters.TryGetValue(param, out var value) ? value : null;


    public static IReadOnlyList<string> SplitToPairs(IReadOnlyList<string> parts)
    {
        var result = new List<string>();
        string? currentParam = null;
        int i = 0;
        while (i < parts.Count)
        {
            string part = parts[i];

            // Если это параметр (начинается с - или --)
            if (part.StartsWith('-'))
            {
                // Если есть текущий параметр, добавляем его в результат
                Add(result, currentParam);

                // Устанавливаем текущий параметр
                currentParam = part;
            }
            else
            {
                // Если это значение и текущий параметр установлен
                if (currentParam != null)
                {
                    if (!currentParam.Contains('=', StringComparison.OrdinalIgnoreCase))
                    {
                        // Если следующее значение не начинается с '-' (нового параметра), объединяем
                        currentParam = $"{currentParam}={part}"; // Объединяем с '='
                    }
                    else
                    {
                        currentParam = $"{currentParam} {part}"; // Объединяем с ' '
                    }
                }
            }
            i++;
        }

        // Добавляем последний параметр, если он есть
        Add(result, currentParam);

        return result;
    }

    private static void Add(List<string> result, string? currentParam)
    {
        if (currentParam != null)
        {
            if (!currentParam.Contains('='))
            {
                result.Add($"{currentParam}=true");
            }
            else
            {
                result.Add(currentParam);
            }
        }
    }
}

