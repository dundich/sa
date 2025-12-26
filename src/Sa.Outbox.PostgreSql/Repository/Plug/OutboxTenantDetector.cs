using Npgsql;
using Sa.Classes;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository.Plug;

internal class OutboxTenantDetector(ISelectTenantCommand command) : IOutboxTenantDetector
{
    public bool CanDetect => true;

    public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        return [.. await ExecuteWithRetry(cancellationToken)];
    }

    private ValueTask<IReadOnlyCollection<int>> ExecuteWithRetry(CancellationToken cancellationToken)
    {
        return Retry.Jitter(
            async ct => await command.Execute(cancellationToken),
            next: (ex, i) => ex is NpgsqlException exception && exception.IsTransient,
            cancellationToken: cancellationToken);
    }
}
