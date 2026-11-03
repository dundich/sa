namespace Sa.Configuration.PostProcessing;

/// <summary>
/// A single step of the final configuration pass: it receives the merged configuration and
/// returns only the keys it changes.
/// </summary>
/// <remarks>
/// Transforms run after every other provider has loaded, in registration order, and each one
/// sees the output of the previous ones. Returning only derived keys — never a copy of the whole
/// configuration — is what lets several transforms (and several <c>AddSaConfiguration</c> calls)
/// coexist: a transform that does not touch a key cannot shadow it.
/// </remarks>
public interface ISaConfigurationTransform
{
    /// <summary>
    /// Produces the key/value pairs this transform changes for <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">The merged configuration, including earlier transforms' output.</param>
    /// <returns>Only the changed pairs; keys left untouched must not be returned.</returns>
    IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration);
}
