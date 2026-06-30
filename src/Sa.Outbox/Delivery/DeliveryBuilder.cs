using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Extensions;
using Sa.Outbox.Delivery.Job;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Sa.Outbox.Delivery;

internal sealed partial class DeliveryBuilder(IServiceCollection services) : IDeliveryBuilder
{

    private IConsumerGroupNamingStrategy _defaultNamingStrategy = new DefaultConsumerGroupNamingStrategy();

    public IDeliveryBuilder AddDeliveryScoped<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        ArgumentNullException.ThrowIfNullOrEmpty(consumerGroupId);
        var sanitized = SanitizeString(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(sanitized, false, configure, jobId);
        return this;
    }

    public IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>
        (Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null, Guid? jobId = null)
          where TConsumer : class, IConsumer<TMessage>
    {
        return AddDeliveryScoped<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure, jobId);
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        ArgumentNullException.ThrowIfNullOrEmpty(consumerGroupId);
        var sanitized = SanitizeString(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(sanitized, true, configure, jobId);
        return this;
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null, Guid? jobId = null)
          where TConsumer : class, IConsumer<TMessage>
    {
        return AddDelivery<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure, jobId);
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

    public string GetConsumerGroupName<TConsumer>() => _defaultNamingStrategy.GetConsumerGroupName<TConsumer>();

    public IDeliveryBuilder ConfigureDefaultNamingStrategy(IConsumerGroupNamingStrategy strategy)
    {
        _defaultNamingStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    sealed class DefaultConsumerGroupNamingStrategy : IConsumerGroupNamingStrategy
    {
        string IConsumerGroupNamingStrategy.GetConsumerGroupName<TConsumer>()
            => $"cg_{(typeof(TConsumer).FullName ?? typeof(TConsumer).Name).GetMurmurHash3()}";
    }

    static string SanitizeString(string input)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(input);
        return SanitazeRegex().Replace(input, "_").ToLower();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex SanitazeRegex();
}
