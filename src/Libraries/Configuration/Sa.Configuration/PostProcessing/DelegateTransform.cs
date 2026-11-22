namespace Sa.Configuration.PostProcessing;

/// <summary>
/// A transform written as a lambda: the same contract as <see cref="ISaConfigurationTransform"/>
/// without a class per step.
/// </summary>
internal sealed class DelegateTransform(
    Func<ISaConfigurationView, IEnumerable<KeyValuePair<string, string?>>> apply)
    : ISaConfigurationTransform
{
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
        => apply(configuration);
}
