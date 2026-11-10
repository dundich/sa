namespace Sa.Outbox.PostgreSql.IdGen;

/// <summary>Creates message ids, ordered by creation time.</summary>
public interface IOutboxIdGenerator
{
    /// <summary>Creates a new id for a message created at <paramref name="timestamp"/>.</summary>
    /// <param name="timestamp">The message creation time.</param>
    /// <returns>A time-ordered id.</returns>
    Guid GenId(DateTimeOffset timestamp);
}
