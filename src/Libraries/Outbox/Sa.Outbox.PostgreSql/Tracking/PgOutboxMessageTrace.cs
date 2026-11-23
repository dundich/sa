using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// One stored message and the state of its delivery in every consumer group that loaded it.
/// </summary>
/// <remarks>
/// An empty <see cref="Deliveries"/> list means the message has been stored but no consumer group has
/// loaded it yet. The provider creates a task row per <c>(message, consumer group)</c> when the group
/// pulls the message, so a state cannot exist before that.
/// </remarks>
public sealed record PgOutboxMessageTrace
{
    /// <summary>The stored message id (<c>msg_id</c>, a v7 UUID).</summary>
    public required Guid MessageId { get; init; }

    /// <summary>
    /// The database-assigned insertion order (<c>msg_seq</c>). This — not the v7 id — is the order the
    /// provider delivers in, so it is also the right key to compare two messages by.
    /// </summary>
    public required long MsgSeq { get; init; }

    /// <summary>The caller-supplied payload id (<c>msg_payload_id</c>). Not unique.</summary>
    public required string PayloadId { get; init; }

    /// <summary>The tenant that owns the message.</summary>
    public required int TenantId { get; init; }

    /// <summary>The message part (the second half of the message-table partition key).</summary>
    public required string Part { get; init; }

    /// <summary>When the message was stored.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>One state per consumer group that has loaded the message, ordered by group id.</summary>
    public required IReadOnlyList<PgOutboxDeliveryState> Deliveries { get; init; }

    /// <summary>Whether at least one consumer group has loaded the message.</summary>
    public bool IsLoaded => Deliveries.Count > 0;

    /// <summary>
    /// The worst-of summary over the groups that loaded the message.
    /// </summary>
    /// <remarks>
    /// A permanent error in any group outranks an in-flight retry in another: the error is the state
    /// that needs attention. Groups that have not loaded the message yet are invisible here — see
    /// <see cref="GetMissingGroups"/>.
    /// </remarks>
    public PgOutboxMessageStage Stage
    {
        get
        {
            if (Deliveries.Count == 0) return PgOutboxMessageStage.NotLoaded;

            bool inFlight = false;

            foreach (PgOutboxDeliveryState delivery in Deliveries)
            {
                if (delivery.StatusCode.IsError()) return PgOutboxMessageStage.Failed;

                if (!delivery.IsTerminal) inFlight = true;
            }

            return inFlight ? PgOutboxMessageStage.InFlight : PgOutboxMessageStage.Settled;
        }
    }

    /// <summary>
    /// Returns the expected groups that have no state for this message yet, preserving the order of
    /// <paramref name="expectedGroups"/>.
    /// </summary>
    /// <remarks>
    /// The provider cannot know which groups <em>should</em> receive a message: a group gets a task row
    /// only when its loader pulls the message, and the registered set lives in DI, not in the storage.
    /// Pass the groups you consider relevant (for example the registry from
    /// <see cref="IPgOutboxMessageTracker.GetConsumerGroupsAsync"/>) to tell "delivered to all of them"
    /// apart from "delivered to the only one that picked it up".
    /// </remarks>
    /// <param name="expectedGroups">The group ids to check for.</param>
    /// <returns>The ids without a state, in the order they were supplied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expectedGroups"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> GetMissingGroups(IEnumerable<string> expectedGroups)
    {
        ArgumentNullException.ThrowIfNull(expectedGroups);

        HashSet<string> loaded = new(StringComparer.Ordinal);

        foreach (PgOutboxDeliveryState delivery in Deliveries)
        {
            _ = loaded.Add(delivery.ConsumerGroup);
        }

        List<string> missing = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string group in expectedGroups)
        {
            if (string.IsNullOrWhiteSpace(group) || loaded.Contains(group) || !seen.Add(group))
                continue;

            missing.Add(group);
        }

        return missing;
    }
}
