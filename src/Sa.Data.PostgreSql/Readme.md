# IPgDataSource

Provides a lightweight (minimal) abstraction for working with PostgreSQL databases in .NET applications.

## ExecuteNonQuery

Executes an SQL query that does not return data (e.g., INSERT, UPDATE, DELETE) and returns the number of affected rows.

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

Reading Data

```csharp
int actual = 0;
await dataSource.ExecuteReader("SELECT 1", (reader, i) => actual = reader.GetInt32(0));
Console.WriteLine($"Value from Database: {actual}");

// get first value
int errCount = await fixture.DataSource.ExecuteReaderFirst<int>("select count(error_id) from outbox__$error");

```

## BeginBinaryImport

Binary Import

```csharp
public async ValueTask<ulong> BulkWrite<TMessage>(ReadOnlyMemory<OutboxMessage<TMessage>> messages CancellationToken cancellationToken){
    // Start binary import
    ulong result = await dataSource.BeginBinaryImport(sqlTemplate, async (writer, t) =>
    {
        // Write rows to import
        WriteRows(writer, typeCode, messages);
        return await writer.CompleteAsync(t);
    }, cancellationToken);

    return result;
}

private void WriteRows<TMessage>(NpgsqlBinaryImporter writer, ReadOnlyMemory<OutboxMessage<TMessage>> messages)
{
    foreach (OutboxMessage<TMessage> message in messages.Span)
    {
        // Generate a unique identifier for the message
        string id = idGenerator.GenId(message.PartInfo.CreatedAt);
    
        // Start a new row for writing
        writer.StartRow();
    
        // Write data to the row
        writer.Write(id, NpgsqlDbType.Char); // id
        writer.Write(message.PartInfo.TenantId, NpgsqlDbType.Integer); // tenant
        writer.Write(message.PartInfo.Part, NpgsqlDbType.Text); // part
    
       // Serialize and write the payload
        using RecyclableMemoryStream stream = streamManager.GetStream();
        serializer.Serialize(stream, message.Payload);
        stream.Position = 0;
        writer.Write(stream, NpgsqlDbType.Bytea); // payload
        writer.Write(stream.Length, NpgsqlDbType.Integer); // payload_size
        writer.Write(message.PartInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint); // created_at
    }
}
```