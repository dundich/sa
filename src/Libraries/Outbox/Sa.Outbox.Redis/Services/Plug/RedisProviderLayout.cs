namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// The provider key a Redis outbox was registered under. Collected by
/// <see cref="RedisMultiProviderGuard"/>, which compares the storage layout of every provider in the
/// container.
/// </summary>
internal sealed record RedisProviderLayout(string ProviderKey);
