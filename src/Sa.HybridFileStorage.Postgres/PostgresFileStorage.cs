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
    string? scopeName,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private readonly string _partName = Sanitize(scopeName ?? "root");

    private readonly string _qualifiedTableName = $"{options.SchemaName}.\"{Sanitize(options.TableName)}\"";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string StorageType => options.StorageType;

    public bool IsReadOnly => options.IsReadOnly;

    public string? ScopeName => scopeName;

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new HybridFileStorageWritableException();
        }
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        EnsureWritable();

        var now = _timeProvider.GetUtcNow();

        await partManager.EnsureParts(_qualifiedTableName, now, [metadata.TenantId, _partName], cancellationToken);


        string fileId = FileIdParser.FormatToFileId(
            StorageType, options.TableName, metadata.TenantId, now, metadata.FileName);

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
INSERT INTO {_qualifiedTableName} (id, name, file_ext, data, size, tenant_id, scope_name, created_at) 
VALUES (@id, @name, @file_ext, @data, @size, @tenant_id, @scope_name, @created_at)
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
                , new NpgsqlParameter<string>("scope_name", _partName)
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
        int rowsAffected = await dataSource.ExecuteNonQuery($"""
            DELETE FROM {_qualifiedTableName}
            WHERE
                tenant_id = @tenant_id AND scope_name = @scope_name
                AND created_at >= @timestamp
                AND id = @id
            """
        ,
        [
            new NpgsqlParameter<int>("tenant_id", tenantId),
            new NpgsqlParameter<string>("scope_name", _partName),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ]
        , cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        (int tenantId, long timestamp) = FileIdParser.ParseFromFileId(fileId, options.TableName);

        int rowsAffected = await dataSource.ExecuteReader(
            $"""
            SELECT data FROM {_qualifiedTableName}
            WHERE
                tenant_id = @tenant_id AND scope_name = @scope_name
                AND created_at >= @timestamp
                AND id = @id
            """
        , async (reader, i) =>
        {
            using var fs = await reader.GetStreamAsync(0, cancellationToken);
            await loadStream(fs, cancellationToken);
        }
        ,
        [
            new NpgsqlParameter<int>("tenant_id", tenantId),
            new NpgsqlParameter<string>("scope_name", _partName),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ]
        , cancellationToken);


        return rowsAffected > 0;
    }


    private static string Sanitize(string tableName)
    {
        var result = new char[tableName.Length];

        for (int i = 0; i < tableName.Length; i++)
        {
            char c = tableName[i];
            result[i] = char.IsLetter(c) || (i > 0 && char.IsDigit(c)) || c == '_' ? c : '_';
        }

        return new string(result);
    }
}
