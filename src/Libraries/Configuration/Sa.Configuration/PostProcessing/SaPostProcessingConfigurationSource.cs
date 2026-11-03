using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Marker for every source of the final configuration pass. The pass rebuilds the "inner"
/// configuration from all the other sources, and skips these by marker rather than by concrete
/// type, so any future post-processing source stays out of its own input too.
/// </summary>
internal interface ISaPostProcessingSource;

/// <summary>
/// Final configuration pass: funnels every provider's values through the registered transforms
/// and keeps only what those transforms changed.
/// </summary>
internal sealed class SaPostProcessingConfigurationSource(
    Func<IConfiguration> getInnerConfiguration,
    SaConfigurationState state) : IConfigurationSource, ISaPostProcessingSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new SaPostProcessingConfigurationProvider(getInnerConfiguration, state);
}
