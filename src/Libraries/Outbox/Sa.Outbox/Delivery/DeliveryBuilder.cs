using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Extensions;
using Sa.Outbox.Delivery.Job;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Delivery;

internal sealed class DeliveryBuilder(IServiceCollection services, string scopeKey) : IDeliveryBuilder
{

    private IConsumerGroupNamingStrategy _defaultNamingStrategy = new DefaultConsumerGroupNamingStrategy();

    public IDeliveryBuilder AddDeliveryScoped<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        EnsureValidConsumerGroupId(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(consumerGroupId, false, configure, jobId, scopeKey, configurationKey);
        return this;
    }

    public IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>
        (Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null, Guid? jobId = null, string? configurationKey = null)
          where TConsumer : class, IConsumer<TMessage>
    {
        return AddDeliveryScoped<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure, jobId, configurationKey);
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        EnsureValidConsumerGroupId(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(consumerGroupId, true, configure, jobId, scopeKey, configurationKey);
        return this;
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null, Guid? jobId = null, string? configurationKey = null)
          where TConsumer : class, IConsumer<TMessage>
    {
        return AddDelivery<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure, jobId, configurationKey);
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

    /// <summary>
    /// Derives the group name for <typeparamref name="TConsumer"/> from the configured naming
    /// strategy, validating it with the same rule as an explicit <c>consumerGroupId</c>.
    /// </summary>
    /// <remarks>
    /// A strategy is a source of identity too: the name it returns is used verbatim as the consumer's
    /// DI key, the settings key, the job name and part of the PostgreSQL partition name. Validating
    /// here keeps the error attributable to the strategy instead of surfacing from the
    /// <c>AddDelivery</c> overload that received the derived name.
    /// </remarks>
    /// <exception cref="ArgumentException">The strategy returned a blank name or one containing a
    /// character outside <c>[a-z0-9_]</c>.</exception>
    public string GetConsumerGroupName<TConsumer>()
        => EnsureValidConsumerGroupId(
            _defaultNamingStrategy.GetConsumerGroupName<TConsumer>(),
            source: "the configured IConsumerGroupNamingStrategy");

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

    /// <summary>
    /// Rejects a consumer group id that is not already a canonical key instead of silently rewriting
    /// it. The id is an identity used verbatim as the consumer's DI key, the settings-registry key,
    /// the job name and part of the PostgreSQL partition name; sanitising it (<c>es-group</c> and
    /// <c>es_group</c> both became <c>es_group</c>, <c>ES_GROUP</c> became <c>es_group</c>) made two
    /// distinct registrations collapse into one, so the caller now gets a loud error.
    /// </summary>
    /// <param name="consumerGroupId">The id supplied to <c>AddDelivery</c>, or returned by an
    /// <see cref="IConsumerGroupNamingStrategy"/>.</param>
    /// <param name="source">
    /// Who supplied the id when it is not the caller of <c>AddDelivery</c> — named in the error so a
    /// naming strategy can be recognised as the origin.
    /// </param>
    /// <returns>The id, unchanged.</returns>
    /// <exception cref="ArgumentException">The id is blank or contains a character outside
    /// <c>[a-z0-9_]</c>.</exception>
    internal static string EnsureValidConsumerGroupId(string? consumerGroupId, string? source = null)
    {
        // The source is appended to both errors: a blank id returned by a naming strategy is exactly
        // the case where `consumerGroupId` names something the caller never passed, and blaming it
        // would hide the real origin (the check used to run before `source` was ever mentioned).
        string suppliedBy = source is null ? string.Empty : $" The id was supplied by {source}.";

        if (string.IsNullOrWhiteSpace(consumerGroupId))
        {
            throw new ArgumentException(
                "Consumer group id must not be null, empty or whitespace. A group id is used " +
                "verbatim as the consumer's DI key, the settings key, the job name and part of " +
                "the PostgreSQL partition name." + suppliedBy,
                nameof(consumerGroupId));
        }

        foreach (char c in consumerGroupId)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                continue;

            throw new ArgumentException(
                $"Consumer group id '{consumerGroupId}' is invalid: the character '{c}' is not " +
                "allowed. A group id is used verbatim as the consumer's DI key, the settings key, " +
                "the job name and part of the PostgreSQL partition name, so it must contain only " +
                "lowercase ASCII letters, digits and underscores (for example 'es_group')." +
                suppliedBy,
                nameof(consumerGroupId));
        }

        return consumerGroupId;
    }
}
