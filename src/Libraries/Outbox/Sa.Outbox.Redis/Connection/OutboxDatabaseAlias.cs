using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Redis.Connection;

/// <summary>
/// Marks the unkeyed <see cref="StackExchange.Redis.IDatabase"/> alias the default provider registers
/// for backward compatibility.
/// </summary>
/// <remarks>
/// Without the marker a provider registered later cannot tell this alias apart from a database the
/// application registered itself, treats it as "already provided" and silently borrows the default
/// provider's connection — so a keyed provider would write into the wrong server or database.
/// </remarks>
internal sealed record OutboxDatabaseAlias(ServiceDescriptor Descriptor);
