using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Registration of the provider is deferred until <c>Commit</c> so that <c>WithEnabled(false)</c>
/// can cancel it. Production code goes through <c>AddSaOutboxUsingPostgreSql</c>, which commits;
/// these tests build a configurator directly, so they commit explicitly.
/// </summary>
internal static class PgConfigurationTestExtensions
{
    public static IPgOutboxConfiguration Commit(this IPgOutboxConfiguration configuration)
    {
        ((PgOutboxConfiguration)configuration).Commit();
        return configuration;
    }
}
