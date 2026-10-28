using Microsoft.Extensions.Configuration;
using Sa.Configuration.CommandLine;
using Sa.Configuration.PostProcessing;
using Sa.Configuration.SecretStore;

namespace Sa.Configuration;

public static class Setup
{
    /// <summary>
    /// Adds the Sa configuration pipeline: command-line overrides plus the final pass that
    /// substitutes <c>{{secret}}</c> placeholders and applies the explicit key aliases.
    /// </summary>
    /// <remarks>
    /// Calling this more than once is safe and intended: every call merges its
    /// <see cref="SecretOptions.KeyAliases"/> into one accumulated map, keeps a single pass and a
    /// single command-line source, and moves the pass to the end of the source list. For a
    /// conflicting canonical key the last call wins, and so do the secret options of the last
    /// call. Secrets are read lazily, on the first build, not at registration.
    /// <para>
    /// A source registered after the last call is still a normal provider: it wins over the pass
    /// for the keys it holds. Call <see cref="FinalizeSaConfiguration"/> as the last registration
    /// step to place the pass after every source.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">A mapping key or alias in
    /// <see cref="SecretOptions.KeyAliases"/> is blank.</exception>
    public static IConfigurationBuilder AddSaConfiguration(
        this IConfigurationBuilder builder,
        SecretOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        options ??= new SecretOptions();

        SaConfigurationState state = SaConfigurationState.GetOrAdd(builder);
        state.Configure(options);

        if (state.CommandLineSource is null)
        {
            state.CommandLineSource = new ArgumentsConfigurationSource { Args = state.Args };
            builder.Add(state.CommandLineSource);
        }
        else if (state.Args is not null)
        {
            // The source is only read when its provider is built, so updating it here still
            // takes effect for a configuration that has not been built yet.
            state.CommandLineSource.Args = state.Args;
        }

        EnsurePostProcessing(builder, state);

        return builder;
    }

    /// <summary>
    /// Appends a custom transform to the final configuration pass.
    /// </summary>
    /// <remarks>
    /// Transforms run after the built-in ones (placeholders, then key aliases) in registration
    /// order, and each one sees the output of the previous ones. A transform returns only the
    /// keys it changes; returning a copy of the whole configuration would shadow every other
    /// provider, including the built-in transforms.
    /// <para>
    /// Registering a transform also registers the pass itself, so this works without a preceding
    /// <see cref="AddSaConfiguration"/>; in that case placeholders are not substituted unless
    /// <see cref="AddSaConfiguration"/> is called as well.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or
    /// <paramref name="transform"/> is null.</exception>
    public static IConfigurationBuilder AddSaConfigurationTransform(
        this IConfigurationBuilder builder,
        ISaConfigurationTransform transform)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(transform);

        SaConfigurationState state = SaConfigurationState.GetOrAdd(builder);
        state.Transforms.Add(transform);
        EnsurePostProcessing(builder, state);

        return builder;
    }

    /// <summary>
    /// Moves the final pass to the end of the source list, after every source registered so far.
    /// </summary>
    /// <remarks>
    /// Use it as the last configuration step when sources are added after the last
    /// <see cref="AddSaConfiguration"/> call — for example a database-backed source that holds
    /// canonical keys. Without it such a source wins over the derived alias values, because the
    /// configuration root resolves the last provider first. Idempotent, and a no-op when the
    /// pipeline was never registered.
    /// </remarks>
    public static IConfigurationBuilder FinalizeSaConfiguration(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Properties.TryGetValue(SaConfigurationState.PropertyKey, out object? value)
            && value is SaConfigurationState state)
        {
            PostProcessingPipeline.MoveToEnd(builder, state);
        }

        return builder;
    }

    private static void EnsurePostProcessing(IConfigurationBuilder builder, SaConfigurationState state)
    {
        if (state.PostProcessingSource is null)
        {
            state.PostProcessingSource = new SaPostProcessingConfigurationSource(
                () => PostProcessingPipeline.BuildInner(builder),
                state);

            builder.Add(state.PostProcessingSource);
            return;
        }

        PostProcessingPipeline.MoveToEnd(builder, state);
    }
}
