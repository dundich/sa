using Microsoft.IO;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;
using System.Runtime.CompilerServices;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// pg://scope/tenant/timestamp/filename
/// </summary>
internal sealed class PostgresFileStorage(
    IPgDataSource dataSource,
    IPartitionManager partManager,
    RecyclableMemoryStreamManager streamManager,
    StorageOptions options,
    string scopeName,
    TimeProvider? timeProvider = null) : IFileStorage
{

    private const string InsertSql =
        """
        INSERT INTO {0} (id, name, file_ext, data, size, tenant_id, scope_name, created_at) 
        VALUES (@id, @name, @file_ext, @data, @size, @tenant_id, @scope_name, @created_at)
        ON CONFLICT (id, tenant_id, scope_name, created_at) DO UPDATE SET
           data = EXCLUDED.data,
           size = EXCLUDED.size,
           created_at = EXCLUDED.created_at
        """;

    private const string DeleteSql =
        """
        DELETE FROM {0}
        WHERE tenant_id = @tenant_id AND scope_name = @scope_name
          AND created_at >= @timestamp AND id = @id
        """;

    private const string SelectSql =
        """
        SELECT data FROM {0}
        WHERE tenant_id = @tenant_id AND scope_name = @scope_name
          AND created_at >= @timestamp AND id = @id
        """;

    private readonly string _partName
        = string.IsNullOrWhiteSpace(scopeName) ? "share" : Sanitize(scopeName);

    private readonly string _qualifiedTableName
        = $"{options.SchemaName}.\"{Sanitize(options.TableName)}\"";

    private readonly string _schemePrefix
        = $"{options.StorageType}{FileIdParser.SchemeSeparator}{options.TableName}/";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string StorageType => options.StorageType;

    public bool IsReadOnly => options.IsReadOnly;

    public string ScopeName => scopeName;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            HybridFileStorageThrowHelper.ThrowWritableException();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanProcess(string? fileId)
    {
        var fileSpan = fileId.AsSpan();

        if (!string.IsNullOrWhiteSpace(fileId)
            && fileSpan.StartsWith(_schemePrefix.AsSpan(), StringComparison.Ordinal)) return false;

        int schemeEnd = fileSpan.IndexOf(FileIdParser.SchemeSeparator.AsSpan());
        if (schemeEnd == -1) return false;

        var afterSpan = fileSpan[(schemeEnd + FileIdParser.SchemeSeparator.Length)..];
        int scopeEnd = afterSpan.IndexOf('/');
        if (scopeEnd == -1) return false;

        var scopeName = afterSpan[..scopeEnd];

        return scopeName.Equals(_partName, StringComparison.Ordinal);
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        EnsureWritable();

        DateTimeOffset createdAtDay = _timeProvider.GetUtcNow().Date;
        long createdAt = createdAtDay.ToUnixTimeSeconds();


        await partManager.EnsureParts(
            _qualifiedTableName,
            createdAtDay,
            [metadata.TenantId, _partName],
            cancellationToken);

        string fileId = FileIdParser.FormatToFileId(
            StorageType, _partName, metadata.TenantId, createdAtDay, metadata.FileName);

        string fileExtension = FileIdParser.GetFileExtension(metadata.FileName);


        if (fileStream is not MemoryStream ms)
        {
            ms = streamManager.GetStream();
            await fileStream.CopyToAsync(ms, cancellationToken);
        }

        try
        {
            if (ms.CanSeek)
            {
                ms.Position = 0;
            }

            var sql = string.Format(InsertSql, _qualifiedTableName);

            await dataSource.ExecuteNonQuery(sql,
            [
                  new NpgsqlParameter<string>("id", fileId)
                , new NpgsqlParameter<string>("name", metadata.FileName)
                , new NpgsqlParameter<string>("file_ext", fileExtension)
                , new NpgsqlParameter("data", fileStream)
                , new NpgsqlParameter<int>("size", (int)ms.Length)
                , new NpgsqlParameter<int>("tenant_id", metadata.TenantId)
                , new NpgsqlParameter<string>("scope_name", _partName)
                , new NpgsqlParameter<long>("created_at", createdAt)
            ], cancellationToken);
        }
        finally
        {
            if (ms != fileStream)
            {
                await ms.DisposeAsync();
            }
        }

        return new StorageResult(fileId, fileId, StorageType, createdAtDay);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        if (!FileIdParser.TryParse(fileId, out _, out int tenantId, out long timestamp, out _))
        {
            return false;
        }

        var sql = string.Format(DeleteSql, _qualifiedTableName);


        int rowsAffected = await dataSource.ExecuteNonQuery(sql,
        [
            new NpgsqlParameter<int>("tenant_id", tenantId),
            new NpgsqlParameter<string>("scope_name", _partName),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ], cancellationToken);

        return rowsAffected > 0;
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        if (!FileIdParser.TryParse(fileId, out _, out int tenantId, out long timestamp, out _))
        {
            return false;
        }

        var sql = string.Format(SelectSql, _qualifiedTableName);

        int rowsAffected = await dataSource.ExecuteReader(sql, async (reader, i) =>
        {
            using var fs = await reader.GetStreamAsync(0, cancellationToken);
            await loadStream(fs, cancellationToken);
        },
        [
            new NpgsqlParameter<int>("tenant_id", tenantId),
            new NpgsqlParameter<string>("scope_name", _partName),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ], cancellationToken);

        return rowsAffected > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Sanitize(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty) return string.Empty;

        Span<char> result = stackalloc char[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            result[i] = char.IsLetter(c) || (i > 0 && char.IsDigit(c)) || c == '_' ? c : '_';
        }

        return new string(result);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Sanitize(string input) => Sanitize(input.AsSpan());

    public Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (!CanProcess(fileId))
            return Task.FromResult<FileMetadata?>(null);

        if (!FileIdParser.TryParse(fileId, out _, out var tenantId, out _, out var fileName))
            return Task.FromResult<FileMetadata?>(null);

        var metadata = new FileMetadata
        {
            ScopeName = _partName,
            StorageType = StorageType,
            FileName = fileName,
            TenantId = tenantId
        };

        return Task.FromResult<FileMetadata?>(metadata);
    }
}
