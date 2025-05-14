using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IO;

namespace Sa.Outbox.PostgreSql.Commands;

internal static class Setup
{
    public static IServiceCollection AddOutboxCommands(this IServiceCollection services)
    {
        services.TryAddSingleton<RecyclableMemoryStreamManager>();
        services.TryAddSingleton<IOutboxBulkCommand, OutboxBulkCommand>();
        services.TryAddSingleton<IStartDeliveryCommand, StartDeliveryCommand>();
        services.TryAddSingleton<IErrorDeliveryCommand, ErrorDeliveryCommand>();
        services.TryAddSingleton<IFinishDeliveryCommand, FinishDeliveryCommand>();
        services.TryAddSingleton<IExtendDeliveryCommand, ExtendDeliveryCommand>();
        return services;
    }
}
