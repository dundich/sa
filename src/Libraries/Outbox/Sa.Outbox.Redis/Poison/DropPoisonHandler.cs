using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sa.Outbox.Redis.Poison;

/// <summary>
/// The default <see cref="IRedisOutboxPoisonHandler"/>: logs a warning and lets the provider
/// acknowledge the entry.
/// </summary>
/// <remarks>
/// Nothing is persisted by the provider. The stream entry itself stays in the message stream until
/// retention trims it, so its payload can still be inspected with <c>XRANGE</c>; replace this
/// handler to keep the message somewhere durable or to raise an alert.
/// </remarks>
internal sealed partial class DropPoisonHandler(ILogger<DropPoisonHandler>? logger = null)
    : IRedisOutboxPoisonHandler
{
    private readonly ILogger _logger = logger ?? NullLogger<DropPoisonHandler>.Instance;

    public ValueTask HandleAsync(RedisOutboxPoisonMessage message, CancellationToken cancellationToken)
    {
        LogDropped(
            _logger,
            message.PayloadType,
            message.PayloadId,
            message.ConsumerGroup,
            message.TenantId,
            message.Part,
            message.Attempt,
            (int)message.StatusCode,
            message.StatusMessage,
            message.Exception);

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(
        EventId = 8201,
        Level = LogLevel.Warning,
        Message = "Redis Outbox dropped poison message {PayloadType}/{PayloadId} (group '{ConsumerGroup}', tenant {TenantId}, part '{Part}', attempt {Attempt}, status {StatusCode}): {StatusMessage}")]
    static partial void LogDropped(
        ILogger logger,
        string payloadType,
        string payloadId,
        string consumerGroup,
        int tenantId,
        string part,
        int attempt,
        int statusCode,
        string statusMessage,
        Exception? exception);
}
