namespace Sa.Outbox.RedisTests;

public sealed record TestMessage(string PayloadId, int Value = 0);
