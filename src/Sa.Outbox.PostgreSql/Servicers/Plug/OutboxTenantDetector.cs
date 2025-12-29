using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Services.Plug;

internal class OutboxTenantDetector(ISelectTenantCommand command) : IOutboxTenantDetector
{
    public bool CanDetect => true;

    public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        return [.. await ExecuteWithRetry(cancellationToken)];
    }

    private ValueTask<IReadOnlyCollection<int>> ExecuteWithRetry(CancellationToken cancellationToken)
    {
        return PgRetryStrategy.ExecuteWithRetry(
            async ct => await command.Execute(cancellationToken),
            cancellationToken: cancellationToken);
    }
}
