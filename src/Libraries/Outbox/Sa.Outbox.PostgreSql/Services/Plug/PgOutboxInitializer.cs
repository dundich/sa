using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.PostgreSql.Services.Plug;

/// <summary>
/// One-time provider warm-up: round-trips <c>SELECT 1</c> on the provider's own data source, so an
/// unavailable database fails a known startup step instead of the first delivery tick.
/// </summary>
internal sealed class PgOutboxInitializer(IPgDataSource dataSource) : IOutboxProviderInitializer
{
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        int result = await dataSource
            .ExecuteScalarTyped<int>("SELECT 1", initCommand: null, cancellationToken)
            .ConfigureAwait(false);

        if (result != 1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL outbox warm-up returned {result} instead of 1.");
        }
    }
}
