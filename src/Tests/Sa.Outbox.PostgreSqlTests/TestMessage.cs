﻿using Sa.Outbox.Support;

namespace Sa.Outbox.PostgreSqlTests;


public class TestMessage : IOutboxPayloadMessage
{
    public static string PartName => "root";

    public string PayloadId { get; set; } = default!;
    public string? Content { get; set; }
    public int TenantId { get; set; }
}