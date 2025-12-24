using Sa.Outbox.Support;

namespace Sa.Outbox.PostgreSqlTests;


internal sealed class TestMessage : IOutboxPayloadMessage
{
    public static string PartName => "root";

    public required string PayloadId { get; set; }
    public string? Content { get; set; }
    public int TenantId { get; set; }
}
