using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Shared plumbing of the final configuration pass: rebuilding the "inner" configuration that
/// feeds it, and keeping the pass last in the source list.
/// </summary>
internal static class PostProcessingPipeline
{
    /// <summary>
    /// Builds the merged configuration the pass reads: every source except the pass itself.
    /// </summary>
    /// <remarks>
    /// Sources are enumerated on every load, not captured at registration, so a provider added
    /// to the builder after <c>AddSaConfiguration</c> is part of the next reload's input.
    /// </remarks>
    public static IConfiguration BuildInner(IConfigurationBuilder builder)
    {
        ConfigurationBuilder inner = new();

        foreach (IConfigurationSource source in builder.Sources)
        {
            if (source is not ISaPostProcessingSource)
                inner.Add(source);
        }

        return inner.Build();
    }

    /// <summary>
    /// Moves the single pass to the end of the source list, so the values it derives win over
    /// every provider registered so far — the configuration root resolves last provider first.
    /// </summary>
    public static void MoveToEnd(IConfigurationBuilder builder, SaConfigurationState state)
    {
        if (state.PostProcessingSource is not { } source)
            return;

        builder.Sources.Remove(source);
        builder.Sources.Add(source);
    }
}
