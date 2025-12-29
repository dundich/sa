namespace Sa.Outbox.Support;


public sealed record PingMessage(string PayloadId, int TenantId = 0) : IOutboxPayloadMessage
{
    public static string PartName => "root";
}
