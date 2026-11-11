namespace Sa.Outbox.PostgreSql.Commands;


/// <summary>One row of the provider's error log.</summary>
/// <param name="ErrorId">The error row id.</param>
/// <param name="TypeName">The recorded exception type name.</param>
/// <param name="CreatedAt">When the error was recorded.</param>
public record struct ErrorInfo(long ErrorId, string TypeName, DateTimeOffset CreatedAt);


internal interface IErrorDeliveryCommand
{
    Task<IReadOnlyDictionary<Exception, ErrorInfo>> ExecuteAsync(
        ReadOnlyMemory<IOutboxContext> messages,
        CancellationToken cancellationToken);
}
