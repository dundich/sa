using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Sa.Outbox.Redis.Configuration;

/// <summary>
/// The single configuration intent recorded for <see cref="RedisOutboxSettings"/>: the
/// <c>WithConfiguration</c> section path, if any.
/// </summary>
/// <remarks>
/// The path is mutable because <c>WithOutboxSettings</c> registers the options pipeline before the
/// caller has a chance to chain <c>WithConfiguration</c>. The configurator reads it when the options
/// are built, so the order of the two calls does not matter.
/// </remarks>
internal sealed class RedisOutboxSettingsState
{
    public string? ConfigSectionPath { get; set; }
}

/// <summary>
/// Applies the configured intents to <see cref="RedisOutboxSettings"/> in the house precedence
/// order: the code actions of <c>WithOutboxSettings</c> first, then the configuration section. The
/// <c>Options(...)</c> actions are registered on the pipeline after this configurator, so they have
/// the last word over both; validation runs after everything.
/// </summary>
internal sealed class RedisOutboxSettingsConfigurator(
    RedisOutboxSettingsState state,
    IServiceProvider services) : IConfigureOptions<RedisOutboxSettings>
{
    public void Configure(RedisOutboxSettings options)
    {
        foreach (Action<IServiceProvider, RedisOutboxSettings> configureAction
            in services.GetServices<Action<IServiceProvider, RedisOutboxSettings>>())
        {
            configureAction.Invoke(services, options);
        }

        if (state.ConfigSectionPath is not { } sectionPath)
            return;

        IConfiguration configuration = services.GetService<IConfiguration>()
            ?? throw new InvalidOperationException(
                $"Redis Outbox settings use WithConfiguration(\"{sectionPath}\"), but no IConfiguration " +
                "is registered. Add one (a host does it automatically) or configure the settings in code.");

        BindFromConfiguration(options, configuration, sectionPath);
    }

    /// <summary>
    /// Concrete binder for <see cref="RedisOutboxSettings"/>. The call site names the settings type,
    /// which is what lets the configuration source generator bake the binding logic under Native AOT.
    /// </summary>
    private static void BindFromConfiguration(
        RedisOutboxSettings options,
        IConfiguration configuration,
        string sectionPath)
        => configuration.GetSection(sectionPath).Bind(options);
}

/// <summary>
/// Validates <see cref="RedisOutboxSettings"/> for the options pipeline.
/// </summary>
/// <remarks>
/// Deliberately <c>IValidateOptions</c> rather than <c>ValidateDataAnnotations()</c>: the latter is
/// marked <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT. Validation runs when the
/// settings are first resolved as well as under <c>ValidateOnStart()</c>, so an invalid value
/// surfaces as an <see cref="OptionsValidationException"/>.
/// </remarks>
internal sealed class RedisOutboxSettingsValidator : IValidateOptions<RedisOutboxSettings>
{
    public ValidateOptionsResult Validate(string? name, RedisOutboxSettings options)
    {
        if (options is null)
            return ValidateOptionsResult.Skip;

        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return ValidateOptionsResult.Fail($"RedisOutboxSettings: {ex.Message}");
        }
    }
}
