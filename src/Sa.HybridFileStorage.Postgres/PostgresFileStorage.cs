using Microsoft.IO;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

internal sealed class PostgresFileStorage(
    IPgDataSource dataSource,
    IPartitionManager partManager,
    RecyclableMemoryStreamManager streamManager,
    StorageOptions options,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private readonly string _qualifiedTableName = $"{options.SchemaName}.\"{options.TableName}\"";

    public string StorageType { get; } = options.StorageType;

    public bool IsReadOnly { get; } = options.IsReadOnly ?? false;

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot perform this operation. The storage is read-only.");
        }
    }

    public async Task<StorageResult> UploadAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        EnsureWritable();

        var now = timeProvider?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();

        await partManager.EnsureParts(_qualifiedTableName, now, [metadata.TenantId], cancellationToken);


        string fileId = FileIdParser.FormatToFileId(StorageType, options.TableName, metadata.TenantId, now, metadata.FileName);
        string fileExtension = FileIdParser.GetFileExtension(metadata.FileName);

        long createdAt = now.ToUnixTimeSeconds();


        if (fileStream is not MemoryStream memoryStream)
        {
            memoryStream = streamManager.GetStream();
            await fileStream.CopyToAsync(memoryStream, cancellationToken);
        }

        try
        {
            if (memoryStream.CanSeek)
            {
                memoryStream.Position = 0;
            }

            await dataSource.ExecuteNonQuery($"""
INSERT INTO {_qualifiedTableName} (id, name, file_ext, data, size, tenant_id, created_at) 
VALUES (@id, @name, @file_ext, @data, @size, @tenant_id, @created_at)
ON CONFLICT DO NOTHING
"""
            ,
            [
                  new NpgsqlParameter<string>("id", fileId)
                , new NpgsqlParameter<string>("name", metadata.FileName)
                , new NpgsqlParameter<string>("file_ext", fileExtension)
                , new NpgsqlParameter("data", fileStream)
                , new NpgsqlParameter<int>("size", (int)memoryStream.Length)
                , new NpgsqlParameter<int>("tenant_id", metadata.TenantId)
                , new NpgsqlParameter<long>("created_at", createdAt)
            ]
            , cancellationToken);
        }
        finally
        {
            if (memoryStream != fileStream)
            {
                await memoryStream.DisposeAsync();
            }
        }

        return new StorageResult(fileId, fileId, StorageType, now);
    }

    public bool CanProcess(string fileId) => fileId.StartsWith($"{StorageType}://{options.TableName}/");

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        (int tenantId, long timestamp) = FileIdParser.ParseFromFileId(fileId, options.TableName);
        int rowsAffected = await dataSource.ExecuteNonQuery(
            $"""
            DELETE FROM {_qualifiedTableName} WHERE tenant_id = @tenant_id AND created_at >= @timestamp AND id = @id
            """
        ,
        [
            new NpgsqlParameter<long>("tenant_id", tenantId),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ]
        , cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<bool> DownloadAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        (int tenantId, long timestamp) = FileIdParser.ParseFromFileId(fileId, options.TableName);

        int rowsAffected = await dataSource.ExecuteReader(
            $"""
            SELECT data FROM {_qualifiedTableName} WHERE tenant_id = @tenant_id AND created_at >= @timestamp AND id = @id
            """
        , async (reader, i) =>
        {
            using var fs = await reader.GetStreamAsync(0, cancellationToken);
            await loadStream(fs, cancellationToken);
        }
        , 
        [
            new NpgsqlParameter<long>("tenant_id", tenantId), 
            new NpgsqlParameter<long>("timestamp", timestamp), 
            new NpgsqlParameter<string>("id", fileId)
        ]
        , cancellationToken);


        return rowsAffected > 0;
    }
}
