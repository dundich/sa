# IPgDataSource

Предоставляет облегченный (минимальный) вариант абстракции для работы с базой данных PostgreSQL в .NET-приложениях.

## ExecuteNonQuery

Выполняет SQL-запрос, который не возвращает данные (например, INSERT, UPDATE, DELETE), и возвращает количество затронутых строк.

```csharp
var dataSource = new PgDataSource(new PgDataSourceSettings("YourConnectionString"));
int affectedRows = await dataSource.ExecuteNonQuery("SELECT 2");
Console.WriteLine($"Affected Rows: {affectedRows}");

var parameters = new[]
{
    new NpgsqlParameter("p1", "Tom"),
    new NpgsqlParameter("p2", 18)
};

int affectedRows = await dataSource.ExecuteNonQuery("""
    CREATE TABLE IF NOT EXISTS users (
        name text,
        age int
    );

    INSERT INTO users (name, age) VALUES (@p1, @p2);
    """, parameters);

Console.WriteLine($"Affected Rows: {affectedRows}");
```

## ExecuteReader 

Чтение данных

```csharp
int actual = 0;
await dataSource.ExecuteReader("SELECT 1", (reader, i) => actual = reader.GetInt32(0));
Console.WriteLine($"Value from Database: {actual}");

// get first value
int errCount = await fixture.DataSource.ExecuteReaderFirst<int>("select count(error_id) from outbox__$error");

```

## BeginBinaryImport

Бинарный импорт

```csharp
public async ValueTask<ulong> BulkWrite<TMessage>(ReadOnlyMemory<OutboxMessage<TMessage>> messages CancellationToken cancellationToken){
    // Начинаем бинарный импорт
    ulong result = await dataSource.BeginBinaryImport(sqlTemplate, async (writer, t) =>
    {
        // Записываем строки в импорт
        WriteRows(writer, typeCode, messages);
        return await writer.CompleteAsync(t);
    }, cancellationToken);

    return result;
}

private void WriteRows<TMessage>(NpgsqlBinaryImporter writer, ReadOnlyMemory<OutboxMessage<TMessage>> messages)
{
    foreach (OutboxMessage<TMessage> message in messages.Span)
    {
        // Генерируем уникальный идентификатор для сообщения
        string id = idGenerator.GenId(message.PartInfo.CreatedAt);
    
        // Начинаем новую строку для записи
        writer.StartRow();
    
        // Записываем данные в строку
        writer.Write(id, NpgsqlDbType.Char); // id
        writer.Write(message.PartInfo.TenantId, NpgsqlDbType.Integer); // tenant
        writer.Write(message.PartInfo.Part, NpgsqlDbType.Text); // part
    
        // Сериализуем и записываем полезную нагрузку
        using RecyclableMemoryStream stream = streamManager.GetStream();
        serializer.Serialize(stream, message.Payload);
        stream.Position = 0;
        writer.Write(stream, NpgsqlDbType.Bytea); // payload
        writer.Write(stream.Length, NpgsqlDbType.Integer); // payload_size
        writer.Write(message.PartInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint); // created_at
    }
}
```