using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Keyed-first resolution of a provider contract.
/// </summary>
/// <remarks>
/// The unkeyed fallback exists only for the default provider key: that is the classic
/// single-provider surface, and it also keeps test containers that register a contract without a key
/// working. For any other key a missing registration is an error instead of a silent switch to the
/// default provider's storage, which would publish or deliver through the wrong provider.
/// </remarks>
internal static class OutboxProviderResolution
{
    /// <summary>Resolves a required provider contract, failing for an unknown non-default key.</summary>
    public static T Required<T>(IServiceProvider services, string providerKey) where T : notnull
    {
        T? keyed = services.GetKeyedService<T>(providerKey);
        if (keyed is not null) return keyed;

        if (IsDefault(providerKey)) return services.GetRequiredService<T>();

        throw new InvalidOperationException(
            $"No outbox provider contract '{typeof(T).Name}' is registered under the key " +
            $"'{providerKey}'. Pass the same scopeKey to AddSaOutbox and the provider package, or " +
            "register the provider with that key instead of relying on the default provider.");
    }

    /// <summary>Resolves an optional provider contract; never falls back for a non-default key.</summary>
    public static T? Optional<T>(IServiceProvider services, string providerKey) where T : class
        => services.GetKeyedService<T>(providerKey)
           ?? (IsDefault(providerKey) ? services.GetService<T>() : null);

    private static bool IsDefault(string providerKey)
        => string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal);
}
