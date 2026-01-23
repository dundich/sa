using System.Diagnostics.CodeAnalysis;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql;

public sealed class PgOutboxConsumeSettings
{
    private readonly Dictionary<string, Guid> _offsets = [];

    public PgOutboxConsumeSettings WithMinOffset(string consumerGroupId, Guid offset)
    {
        _offsets[consumerGroupId] = offset;
        return this;
    }

    public PgOutboxConsumeSettings WithMinOffset(string consumerGroupId, DateTimeOffset offset)
    {
        _offsets[consumerGroupId] = Guid.CreateVersion7(offset).ToMinGuidV7();
        return this;
    }

    public PgOutboxConsumeSettings WithMinOffset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer>(Guid offset)
        => WithMinOffset(IDeliveryBuilder.GetConsumerGroupName<TConsumer>(), offset);

    public PgOutboxConsumeSettings WithMinOffset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer>(DateTimeOffset offset)
        => WithMinOffset(IDeliveryBuilder.GetConsumerGroupName<TConsumer>(), offset);

    public Guid GetMinOffset(string consumerGroupId)
    {
        return _offsets.TryGetValue(consumerGroupId, out var result)
            ? result
            : Guid.Empty;
    }
}
