using System.Text;
using System.Text.RegularExpressions;

namespace Sa.Configuration.SecretStore.Engine;

internal sealed partial class SecretService(ISecretStore secretStore) : ISecretService
{
    private static readonly Regex s_placeholder = PlaceholderRegex();

    private const int MaxLoopCount = 3;

    public string? GetSecret(string key) => secretStore.GetSecret(key);

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false)
    {
        if (string.IsNullOrWhiteSpace(inputString)) return null;

        StringBuilder result = new(inputString);

        int currentPosition = 0;
        int depth = 0;
        int previousInsertEnd = -1;

        while (currentPosition < result.Length)
        {
            Match match = s_placeholder.Match(result.ToString(), currentPosition);
            if (!match.Success)
                break;

            // The search resumes at the start of the region we just inserted (currentPosition == match.Index).
            // A match inside that region means we are resolving a placeholder embedded in the value we
            // inserted — one level deeper. A match past the region is a sibling placeholder — depth resets.
            depth = match.Index < previousInsertEnd ? depth + 1 : 0;

            if (depth > MaxLoopCount)
            {
                // Detect infinite loop - the replacement kept generating the same placeholder
                throw new InvalidOperationException(
                    $"Maximum replacement depth ({MaxLoopCount}) exceeded for secret '{match.Groups[2].Value}'. " +
                    "This might indicate a circular reference or a secret that keeps generating new placeholders.");
            }

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

            previousInsertEnd = match.Index + resolvedSecretValue.Length;
            currentPosition = match.Index;
        }

        var finalResult = result.ToString();

        return string.IsNullOrWhiteSpace(finalResult) ? null : finalResult;
    }

    private static string NormalizeValue(string secretValue)
    {
        if (string.IsNullOrEmpty(secretValue))
            return secretValue;
        return secretValue.Trim().Trim('\'', '"', '`');
    }

    [GeneratedRegex(@"\{\{(\?)?(\w+)\}\}", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    internal static partial Regex PlaceholderRegex();
}
