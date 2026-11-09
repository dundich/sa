namespace Sa.Configuration.SecretStore;

public interface ISecretService
{
    /// <summary>
    /// Replaces <c>{{key}}</c> placeholders in <paramref name="inputString"/> with secret values.
    /// </summary>
    /// <param name="inputString">String to resolve; <see langword="null"/> or blank yields <see langword="null"/>.</param>
    /// <param name="returnNullIfSecretNotFound">
    /// When <see langword="true"/>, an unresolved placeholder makes the whole result
    /// <see langword="null"/> instead of throwing.
    /// </param>
    /// <returns>The resolved string, or <see langword="null"/> for blank input or a tolerated miss.</returns>
    /// <exception cref="ArgumentException">
    /// A non-optional placeholder could not be resolved by any store or
    /// <see cref="SecretResolutionOptions.Resolver"/>.
    /// </exception>
    string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false);

    /// <summary>
    /// Gets a raw secret value without placeholder processing.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <returns>
    /// The value, or <see langword="null"/> when neither the stores nor
    /// <see cref="SecretResolutionOptions.Resolver"/> produced one.
    /// </returns>
    string? GetSecret(string key);
}
