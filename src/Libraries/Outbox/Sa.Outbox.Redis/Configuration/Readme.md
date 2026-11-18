# Settings

**English** | [Русский](Readme-ru.md)

Settings layers, keys, retention and poison-message handling for the Redis provider. Part of the [`Sa.Outbox.Redis`](../Readme.md) documentation.

## Contents

- [Retention](#retention)
- [Poison messages](#poison-messages)

The settings object is a standard `Microsoft.Extensions.Options` instance. Three layers are applied
in order, each overriding the previous one:

1. code defaults passed to `WithOutboxSettings`;
2. the configuration section passed to `WithConfiguration`;
3. raw `Options(...)` actions.

Validation runs when the settings are first resolved, and at host start when the container validates
options (`ValidateOnStart`). An invalid value surfaces as an `OptionsValidationException`.

```csharp
cfg.WithConfiguration("Sa:Outbox:Redis");

cfg.WithOutboxSettings((sp, settings) =>
{
    settings.KeySettings.Prefix = "sa:outbox";
    settings.KeySettings.Separator = ":";

    settings.ConsumeSettings.StartFrom = RedisOutboxGroupStart.Lookback; // Beginning | Now
    settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMinutes(10);
    settings.ConsumeSettings.MaxDeliveryAttempts = 5;
});

// Escape hatch over both: the last word on the standard options pipeline.
cfg.Options(options => options.Configure(settings => settings.RetentionSettings.Enabled = true));
```

```json
{
  "Sa": {
    "Outbox": {
      "Redis": {
        "KeySettings": { "Prefix": "sa:outbox", "Separator": ":" },
        "ConsumeSettings": {
          "StartFrom": "Lookback",
          "PendingClaimIdle": "00:10:00",
          "MaxDeliveryAttempts": 5
        },
        "RetentionSettings": {
          "Enabled": true,
          "StreamMaxAge": "7.00:00:00",
          "TrimInterval": "00:05:00",
          "Approximate": true,
          "TrimLimit": 10000
        }
      }
    }
  }
}
```

`StartFrom` only matters when a group is created on an existing stream: `Lookback` starts at
`OutboxMessageFilter.FromDate` (the group's lookback window), `Beginning` at `0-0`, `Now` at `$`.

`PendingClaimIdle` is the only retry clock: an entry that was not acknowledged — a failure, a
postponement, a crashed worker — becomes claimable again after it has been idle that long. Keep it
comfortably above the consumer's `LockDuration`. `MaxDeliveryAttempts` bounds how many times such an
entry may be claimed before it is handed to the poison handler.

## Retention

Automatic trimming is **off by default**. When `Enabled` is set, it runs inline after a publish
batch, throttled per stream by `TrimInterval` (5 minutes by default) — a stream that stops growing is
simply not trimmed.

A message stream is never trimmed by raw length. The provider computes a safe floor from the groups
of the stream (`XINFO GROUPS`, and `XPENDING` when a group has pending entries):

- a group with pending entries contributes its oldest pending id;
- otherwise it contributes its `last-delivered-id` (everything before it was delivered and
  acknowledged);
- a group that has consumed nothing yet (`last-delivered-id = 0-0`) blocks trimming, because it may
  still start at the beginning.

The floor is the smallest contribution across all groups, and the removed range is
`min(safeFloor, now - StreamMaxAge)`: an entry must be older than the retention window **and**
needed by no group. `StreamMaxAge = 0` disables the age limit but keeps the safe floor. A stream with
no group is not trimmed at all, so a group created later with `StartFrom = Beginning` starts on a
complete log.

`Approximate = true` uses `XTRIM ... ~`, which is much cheaper for Redis and may leave slightly more
entries than the limit. `TrimLimit` maps to `XTRIM ... LIMIT` and requires the approximate mode
(a non-zero `TrimLimit` with `Approximate = false` fails validation); `0` removes the limit.

## Poison messages

There is no dead-letter stream. When the provider stops delivering a message — its pending delivery
counter exceeded `ConsumeSettings.MaxDeliveryAttempts`, or the consumer reported a permanent outcome
— it calls `IRedisOutboxPoisonHandler` and, if the handler returns normally, acknowledges the entry.

The default handler logs a warning and drops the message. Replace it to persist the message yourself:

```csharp
cfg.WithPoisonHandler<MyPoisonHandler>();          // resolved from DI
cfg.WithPoisonHandler(new MyPoisonHandler());      // instance
cfg.WithPoisonHandler(sp => new MyPoisonHandler(sp.GetRequiredService<MyStore>()));
```

```csharp
public sealed class MyPoisonHandler(MyStore store) : IRedisOutboxPoisonHandler
{
    public ValueTask Handle(RedisOutboxPoisonMessage message, CancellationToken cancellationToken)
        => store.Save(message.PayloadId, message.PayloadType, message.StatusCode,
                      message.Exception, message.Payload, cancellationToken);
}
```

The handler runs **before** `XACK`. Returning normally means the provider may acknowledge; throwing
means the entry is left pending and will be claimed again, so handling is at-least-once and the
handler must tolerate duplicates. The entry body itself stays in the message stream until retention
trims it, so even a dropped message can still be inspected with `XRANGE` for a while.
