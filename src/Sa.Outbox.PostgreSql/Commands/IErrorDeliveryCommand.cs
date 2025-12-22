namespace Sa.Outbox.PostgreSql.Commands;


public record struct ErrorInfo(long ErrorId, string TypeName, DateTimeOffset CreatedAt);


internal interface IErrorDeliveryCommand
{
    Task<IReadOnlyDictionary<Exception, ErrorInfo>> Execute(
        ReadOnlyMemory<IOutboxContext> messages, 
        CancellationToken cancellationToken);
}
