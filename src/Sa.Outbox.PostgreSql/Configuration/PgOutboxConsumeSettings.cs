using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Configuration;

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

    public Guid GetMinOffset(string consumerGroupId)
    {
        return _offsets.TryGetValue(consumerGroupId, out var result)
            ? result
            : Guid.Empty;
    }
}
