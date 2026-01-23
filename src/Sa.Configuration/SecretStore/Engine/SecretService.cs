using System.Text;
using System.Text.RegularExpressions;

namespace Sa.Configuration.SecretStore.Engine;

internal sealed partial class SecretService(ISecretStore secretStore) : ISecretService
{
    private static readonly Regex s_placeholder = PlaceholderRegex();

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false)
    {
        if (string.IsNullOrWhiteSpace(inputString)) return null;

        var result = new StringBuilder(inputString);
        int currentPosition = 0;
        Match placeholderMatch;
        do
        {
            placeholderMatch = s_placeholder.Match(result.ToString(), currentPosition);
            if (placeholderMatch.Success)
            {
                bool isOptional = placeholderMatch.Groups[1].Success;
                string secretName = placeholderMatch.Groups[2].Value;

                string? resolvedSecretValue = secretStore.GetSecret(secretName);

                if (resolvedSecretValue == null)
                {
                    if (returnNullIfSecretNotFound || isOptional)
                    {
                        return null;
                    }
                    else
                    {
                        throw new ArgumentException($"The secret name '{secretName}' was not present in vault. Ensure that you have a local `secrets.txt` file in the src folder.");
                    }
                }

                resolvedSecretValue = NormalizeValue(resolvedSecretValue);

                result.Replace(placeholderMatch.Value, resolvedSecretValue, placeholderMatch.Index, placeholderMatch.Length);
                currentPosition = placeholderMatch.Index;
            }
        }
        while (placeholderMatch.Success && IsSearchPositionValid(result.ToString(), currentPosition));

        var str = result.ToString();

        return string.IsNullOrWhiteSpace(str) ? null : str;
    }

    private static bool IsSearchPositionValid(string inputString, int currentPosition) => inputString.Length >= currentPosition;

    private static string NormalizeValue(string secretValue) => secretValue.Trim().Trim('\'', '"', '`');
}
