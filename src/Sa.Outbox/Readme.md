# Outbox

The base logic and abstractions designed for implementing the Outbox pattern, with support for partitioning.



## 🚀 Подключение за 3 шага

### 1. Установите пакет провайдера
```bash
# Для SQL Server
Install-Package Sa.Outbox.SqlServer
```

### 2. Настройте в Program.cs
```csharp
// Minimal API
builder.Services.AddOutbox();
```


## 🔧 Реализация своего провайдера

### 1. Создайте класс плагина
```csharp
public class MyCustomOutboxPlugin : IOutboxPlugin
{
    public string Name => "MyCustom";
    public string Version => "1.0";
    public string Provider => "MyDatabase";
    
    public IOutboxBulkWriter BulkWriter { get; }
    public IOutboxDeliveryManager DeliveryManager { get; }
    public IOutboxTenantDetector TenantDetector { get; }
    
    public MyCustomOutboxPlugin(string connectionString, ILogger logger)
    {
        BulkWriter = new MyBulkWriter(connectionString, logger);
        DeliveryManager = new MyDeliveryManager(connectionString, logger);
        TenantDetector = new MyTenantDetector(connectionString, logger);
    }
    
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
```

### 2. Реализуйте обязательные интерфейсы:
- **`IOutboxBulkWriter`** - массовая запись сообщений
- **`IOutboxDeliveryManager`** - управление доставкой
- **`ITenantSource`** - поддержка мультитенантности

### 3. Зарегистрируйте плагин
```csharp
builder.Services.AddSingleton<IOutboxPlugin>(new MyCustomOutboxPlugin(
    connectionString,
    loggerFactory.CreateLogger<MyCustomOutboxPlugin>()));
```

## 📊 Примеры использования

### Bulk запись сообщений
```csharp
var messages = orders.Select(order => new OutboxMessage<OrderCreated>(
    Guid.NewGuid(),
    order.TenantId,
    new OrderCreated(order.Id),
    DateTimeOffset.UtcNow)).ToArray();

var savedCount = await _outbox.InsertBulk(messages);
```

### Получение сообщений для обработки
```csharp
var buffer = new IOutboxContextOperations<OrderCreated>[100];
var count = await _deliveryManager.RentDelivery(
    buffer,
    TimeSpan.FromMinutes(5),
    new OutboxMessageFilter { TenantId = "tenant-123" });
```

## 🛠 Доступные провайдеры
- ✅ **PostgreSQL** - `Sa.Outbox.Postgres`
- ✅ **Redis** - `Sa.Outbox.Redis` (в разработке)
- 🛠 **Ваша реализация** - создайте свой плагин

## 📝 Требования к реализации
1. **Idempotency** - гарантия однократной доставки
2. **Transactional** - согласованность с бизнес-операциями
3. **Tenant-aware** - поддержка изоляции клиентов
4. **Async** - полная асинхронность

## 🆘 Поддержка
- Документация: [docs.sa.outbox](https://docs.sa.outbox)
- Примеры: [github.com/sa-outbox/examples](https://github.com/sa-outbox/examples)
- Issues: [github.com/sa-outbox/sa.outbox/issues](https://github.com/sa-outbox/sa.outbox/issues)

