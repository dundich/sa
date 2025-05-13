using System.Text.RegularExpressions;

namespace Sa.Configuration.SecretStore.Engine;

internal partial class SecretService(ISecretStore secretStore) : ISecretService
{
    private static readonly Regex s_placeholder = PlaceholderRegex();

    public string? PopulateSecrets(string? inputString)
    {
        if (string.IsNullOrWhiteSpace(inputString)) return null;

        int currentPosition = 0;
        Match placeholderMatch;
        do
        {
            placeholderMatch = s_placeholder.Match(inputString, currentPosition);
            if (placeholderMatch.Success)
            {
                string secretName = placeholderMatch.Groups[1].Value;
                string resolvedSecretValue = secretStore.GetSecret(secretName)
                    ?? throw new ArgumentException($"The secret name '{secretName}' was not present in vault. Ensure that you have a local `secrets.production.txt` file in the src folder.");

                resolvedSecretValue = NormalizeValue(resolvedSecretValue);

                inputString = inputString.Replace(placeholderMatch.Value, resolvedSecretValue, StringComparison.InvariantCulture);
                currentPosition = placeholderMatch.Index + 1;
            }
        }
        while (placeholderMatch.Success && IsSearchPositionValid(inputString, currentPosition));

        return string.IsNullOrWhiteSpace(inputString) ? null : inputString;
    }

    private static bool IsSearchPositionValid(string inputString, int currentPosition) => inputString.Length >= currentPosition;

    private static string NormalizeValue(string secretValue) => secretValue.Trim().Trim('\'', '"');
}
