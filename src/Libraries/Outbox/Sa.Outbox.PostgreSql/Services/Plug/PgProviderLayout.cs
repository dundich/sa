namespace Sa.Outbox.PostgreSql.Services.Plug;

/// <summary>
/// The provider key a PostgreSQL outbox was registered under. Collected by
/// <see cref="PgMultiProviderGuard"/>, which compares the storage layout of every provider in the
/// container.
/// </summary>
internal sealed record PgProviderLayout(string ProviderKey);
