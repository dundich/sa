
namespace Sa.Outbox.PostgreSql.Commands;

internal interface ISelectTenantCommand
{
    Task<IReadOnlyCollection<int>> ExecuteAsync(CancellationToken cancellationToken);
}
