using StackExchange.Redis;

namespace Sa.Outbox.Redis.Connection;

/// <summary>
/// Supplies the <see cref="IDatabase"/> every Redis Outbox component talks to. Resolution is lazy,
/// so a publish-only container never opens a connection until the first message is written.
/// </summary>
public interface IRedisOutboxConnectionProvider
{
    /// <summary>Gets the database the Outbox uses. Throws when no connection source is configured.</summary>
    IDatabase Database { get; }

    /// <summary>
    /// Gets a value indicating whether the multiplexer behind <see cref="Database"/> was created by
    /// this provider (and is therefore disposed with it).
    /// </summary>
    bool IsOwner { get; }
}
