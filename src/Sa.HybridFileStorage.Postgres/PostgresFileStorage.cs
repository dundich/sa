using Microsoft.IO;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.Postgres;

internal class PostgresFileStorage(
    IPgDataSource dataSource,
    IPartitionManager partManager,
    ICurrentTimeProvider currentTime,
    RecyclableMemoryStreamManager streamManager,
    StorageOptions options
) : IFileStorage
{

    private readonly string _qualifiedTableName = $"{options.SchemaName}.\"{options.TableName.Trim('"')}\"";

    public string StorageType => options.StorageType;

    public bool IsReadOnly => options.IsReadOnly;

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

        var now = currentTime.GetUtcNow();

        await partManager.EnsureParts(_qualifiedTableName, now, [metadata.TenantId], cancellationToken);


        string fileId = Parser.FormatToFileId(StorageType, metadata.TenantId, now, metadata.FileName);
        string fileExtension = Parser.GetFileExtension(metadata.FileName);

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
                  new("id", fileId)
                , new("name", metadata.FileName)
                , new("file_ext", fileExtension)
                , new("data", fileStream)
                , new("size", (int)memoryStream.Length)
                , new("tenant_id", metadata.TenantId)
                , new("created_at", createdAt)
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

    public bool CanProcess(string fileId) => fileId.StartsWith($"{StorageType}:://");

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        (int tenantId, long timestamp) = Parser.ParseFromFileId(fileId);
        int rowsAffected = await dataSource.ExecuteNonQuery(
            $"""
            DELETE FROM {_qualifiedTableName} WHERE tenant_id = @tenant_id AND created_at >= @timestamp AND id = @id
            """
        , [new("tenant_id", tenantId), new("timestamp", timestamp), new("id", fileId)]
        , cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<bool> DownloadAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        (int tenantId, long timestamp) = Parser.ParseFromFileId(fileId);

        int rowsAffected = await dataSource.ExecuteReader(
            $"""
            SELECT data FROM {_qualifiedTableName} WHERE tenant_id = @tenant_id AND created_at >= @timestamp AND id = @id
            """
        , async (reader, i) =>
        {
            using var fs = await reader.GetStreamAsync(0, cancellationToken);
            await loadStream(fs, cancellationToken);
        }
        , [new("tenant_id", tenantId), new("timestamp", timestamp), new("id", fileId)]
        , cancellationToken);


        return rowsAffected > 0;
    }
}
