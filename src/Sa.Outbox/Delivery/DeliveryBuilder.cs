using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery.Job;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Sa.Outbox.Delivery;

internal sealed partial class DeliveryBuilder(IServiceCollection services) : IDeliveryBuilder
{
    public IDeliveryBuilder AddDeliveryScoped<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, ConsumerGroupSettings>? configure = null,
        Guid? jobId = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        ArgumentNullException.ThrowIfNullOrEmpty(consumerGroupId);
        var sanitized = SanitizeString(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(sanitized, false, configure, jobId);
        return this;
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, ConsumerGroupSettings>? configure = null,
        Guid? jobId = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        ArgumentNullException.ThrowIfNullOrEmpty(consumerGroupId);
        var sanitized = SanitizeString(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(sanitized, true, configure, jobId);
        return this;
    }

    public IDeliveryBuilder AddDeliveryBatching<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
        where TImplementation : class, IDeliveryBatcher
    {
        services
            .RemoveAll<IDeliveryBatcher>()
            .TryAddSingleton<IDeliveryBatcher, TImplementation>();
        return this;
    }

    static string SanitizeString(string input)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(input);
        return SanitazeRegex().Replace(input, "_").ToLower();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex SanitazeRegex();
}
