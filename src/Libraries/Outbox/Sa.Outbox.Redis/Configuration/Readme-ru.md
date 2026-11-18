# Настройки

**Русский** | [English](Readme.md)

Слои настроек, ключи, обрезка и обработка «ядовитых» сообщений у Redis-провайдера. Часть документации [`Sa.Outbox.Redis`](../Readme-ru.md).

## Содержание

- [Обрезка](#обрезка)
- [«Ядовитые» сообщения](#ядовитые-сообщения)

Объект настроек — обычный экземпляр `Microsoft.Extensions.Options`. Слои применяются по порядку,
каждый перекрывает предыдущий:

1. значения по умолчанию из кода в `WithOutboxSettings`;
2. секция конфигурации, переданная в `WithConfiguration`;
3. действия `Options(...)` — прямой доступ к конвейеру.

Валидация выполняется при первом разрешении настроек и при старте хоста, если контейнер проверяет
options (`ValidateOnStart`). Некорректное значение проявляется как `OptionsValidationException`.

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

// Выход за оба слоя: последнее слово на стандартном конвейере options.
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

`StartFrom` важен только при создании группы на существующем stream: `Lookback` — от
`OutboxMessageFilter.FromDate` (окно lookback группы), `Beginning` — с `0-0`, `Now` — с `$`.

`PendingClaimIdle` — единственные часы повторов: запись, которая не была подтверждена (ошибка,
отложена, упавший воркер), снова станет доступной после того, как простаивала столько времени.
Держите значение заметно больше `LockDuration` consumer-а. `MaxDeliveryAttempts` ограничивает,
сколько раз такую запись можно забрать, прежде чем передать её в poison handler.

## Обрезка

Автоматическая обрезка по умолчанию **выключена**. Если задать `Enabled`, она выполняется прямо
после батча публикации с троттлингом на stream по `TrimInterval` (5 минут по умолчанию): stream,
который перестал расти, просто не обрезается.

Stream сообщений никогда не режется по голой длине. Провайдер вычисляет безопасную границу по
группам этого stream (`XINFO GROUPS`, а при наличии pending — ещё и `XPENDING`):

- группа с pending-записями даёт свой самый старый pending id;
- без pending — свой `last-delivered-id` (всё до него доставлено и подтверждено);
- группа, которая ещё ничего не прочитала (`last-delivered-id = 0-0`), блокирует обрезку: она может
  начать с начала.

Граница — минимум по всем группам, а удаляется диапазон `min(safeFloor, now - StreamMaxAge)`:
запись должна быть старше окна хранения **и** не нужна ни одной группе. `StreamMaxAge = 0` отключает
возрастной предел, но сохраняет безопасную границу. Stream без групп не обрезается вовсе, чтобы
группа, созданная позже с `StartFrom = Beginning`, увидела полный лог.

`Approximate = true` использует `XTRIM ... ~`: это заметно дешевле для Redis и может оставить чуть
больше записей, чем задано. `TrimLimit` соответствует `XTRIM ... LIMIT` и требует приблизительного
режима (ненулевой `TrimLimit` при `Approximate = false` не проходит валидацию); `0` снимает предел.

## «Ядовитые» сообщения

Dead-letter stream нет. Когда провайдер перестаёт доставлять сообщение — счётчик доставок в PEL
превысил `ConsumeSettings.MaxDeliveryAttempts` или consumer вернул постоянную ошибку — он вызывает
`IRedisOutboxPoisonHandler` и, если обработчик завершился успешно, подтверждает запись (`XACK`).

Стандартный обработчик пишет Warning и выбрасывает сообщение. Чтобы сохранять его самому, замените
обработчик:

```csharp
cfg.WithPoisonHandler<MyPoisonHandler>();          // из DI
cfg.WithPoisonHandler(new MyPoisonHandler());      // экземпляр
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

Обработчик вызывается **до** `XACK`. Успешный возврат означает «можно подтверждать»; исключение
оставляет запись pending, и она будет забрана снова — то есть обработка at-least-once, дубликаты
нужно терпеть. Сама запись остаётся в stream до обрезки retention, поэтому даже выброшенное
сообщение ещё какое-то время можно посмотреть через `XRANGE`.
