using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Per-consumer-group floors for the consumption cursor.
/// </summary>
/// <remarks>
/// Since the C2 fix the cursor is a <c>msg_seq</c> (BIGINT, database-assigned insertion order),
/// so a floor configured as a v7 <see cref="Guid"/> or as a <see cref="DateTimeOffset"/> has to be
/// translated into a seq before it can be compared. The translation needs a database round trip,
/// which this settings object cannot perform on its own — <see cref="OutboxTaskLoader"/> resolves
/// the floor on first load per group and caches the resolved seq here.
/// </remarks>
public sealed class PgOutboxConsumeSettings
{
    /// <summary>Resolved seq floors, keyed by consumer group id. Filled by the task loader.</summary>
    private readonly Dictionary<string, long> _resolvedFloors = [];

    /// <summary>Legacy floors given as a v7 message id, keyed by consumer group id.</summary>
    private readonly Dictionary<string, Guid> _guidFloors = [];

    /// <summary>Floors given as a date, keyed by consumer group id.</summary>
    private readonly Dictionary<string, DateTimeOffset> _dateFloors = [];

    /// <summary>Legacy overload, kept for API compatibility: "start consuming from this message onward".</summary>
    public PgOutboxConsumeSettings WithMinOffset(string consumerGroupId, Guid offset)
    {
        _guidFloors[consumerGroupId] = offset;
        return this;
    }

    public PgOutboxConsumeSettings WithMinOffset(string consumerGroupId, DateTimeOffset offset)
    {
        _dateFloors[consumerGroupId] = offset;
        return this;
    }

    internal bool TryGetResolvedFloor(string consumerGroupId, out long floor)
        => _resolvedFloors.TryGetValue(consumerGroupId, out floor);

    internal void SetResolvedFloor(string consumerGroupId, long floor)
        => _resolvedFloors[consumerGroupId] = floor;

    internal bool TryGetGuidFloor(string consumerGroupId, out Guid floor)
        => _guidFloors.TryGetValue(consumerGroupId, out floor);

    internal bool TryGetDateFloor(string consumerGroupId, out DateTimeOffset floor)
        => _dateFloors.TryGetValue(consumerGroupId, out floor);
}