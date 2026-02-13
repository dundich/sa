using System.Text;
using System.Text.RegularExpressions;

namespace Sa.Configuration.SecretStore.Engine;

internal sealed partial class SecretService(ISecretStore secretStore) : ISecretService
{
    private static readonly Regex s_placeholder = PlaceholderRegex();

    private const int MaxLoopCount = 3;

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false)
    {
        if (string.IsNullOrWhiteSpace(inputString)) return null;

        StringBuilder result = new(inputString);

        int currentPosition = 0;
        int loopCounter = MaxLoopCount;
        do
        {
            Match match = s_placeholder.Match(result.ToString(), currentPosition);
            if (!match.Success)
                break;

            bool isOptional = returnNullIfSecretNotFound || match.Groups[1].Success;
            string secretName = match.Groups[2].Value;

            string? resolvedSecretValue = secretStore.GetSecret(secretName);

            if (resolvedSecretValue == null)
            {
                if (isOptional)
                {
                    return null;
                }

                throw new ArgumentException($"The secret '{secretName}' not found in secret store."
                    + "Ensure the secret exists in the configured source.");
            }

            resolvedSecretValue = NormalizeValue(resolvedSecretValue);

            result.Replace(match.Value, resolvedSecretValue, match.Index, match.Length);

            if (currentPosition == match.Index)
            {
                if (--loopCounter < 0)
                {
                    // Detect infinite loop - the replacement might contain the same placeholder
                    throw new InvalidOperationException(
                        $"Maximum replacement depth ({MaxLoopCount}) exceeded for secret '{secretName}'. " +
                        "This might indicate a circular reference or a secret that keeps generating new placeholders.");
                }
            }
            else
            {
                loopCounter = 0;
            }

            currentPosition = match.Index;

        }
        while (IsSearchPositionValid(result.ToString(), currentPosition));

        var finalResult = result.ToString();

        return string.IsNullOrWhiteSpace(finalResult) ? null : finalResult;
    }

    private static bool IsSearchPositionValid(string inputString, int currentPosition)
        => inputString.Length >= currentPosition;

    private static string NormalizeValue(string secretValue)
    {
        if (string.IsNullOrEmpty(secretValue))
            return secretValue;
        return secretValue.Trim().Trim('\'', '"', '`');
    }

    [GeneratedRegex(@"\{\{(\?)?(\w+)\}\}", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    internal static partial Regex PlaceholderRegex();
}
