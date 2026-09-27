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
    PostgresFileStorageOptions options,
    TimeProvider? timeProvider = null) : IFileStorage
{

    private const string InsertSql =
        """
        INSERT INTO {0} (id, name, file_ext, data, size, tenant_id, basket, created_at)
        VALUES (@id, @name, @file_ext, @data, @size, @tenant_id, @basket, @created_at)
        ON CONFLICT (id, tenant_id, basket, created_at) DO UPDATE SET
           data = EXCLUDED.data,
           size = EXCLUDED.size,
           created_at = EXCLUDED.created_at
        """;

    private const string DeleteSql =
        """
        DELETE FROM {0}
        WHERE tenant_id = @tenant_id AND basket = @basket
          AND created_at >= @timestamp AND id = @id
        """;

    private const string SelectSql =
        """
        SELECT data FROM {0}
        WHERE tenant_id = @tenant_id AND basket = @basket
          AND created_at >= @timestamp AND id = @id
        """;

    // The options reach this type only via the registration extension, which calls
    // PostgresFileStorageOptions.Validate() first. That guarantees Basket, TableName and
    // StorageType are already bare words and SchemaName is a single identifier, so they are
    // used verbatim. Rewriting them here (the previous Sanitize) made the DDL registered under
    // the raw name and the queries under the rewritten one, and made StorageType disagree with
    // _schemePrefix — a file ID this storage produced could not be processed by it.
    private readonly string _partName = options.Basket;

    private readonly string _qualifiedTableName
        = $"\"{options.SchemaName ?? PostgresFileStorageSchema.FallbackSchema}\".\"{options.TableName}\"";

    private readonly string _schemePrefix
        = $"{options.StorageType}{FileIdParser.SchemeSeparator}{options.Basket}/";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string StorageType => options.StorageType;

    public bool IsReadOnly => options.IsReadOnly;

    public string Basket => options.Basket;


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

        if (string.IsNullOrWhiteSpace(fileId)
            || !fileSpan.StartsWith(_schemePrefix.AsSpan(), StringComparison.Ordinal)) return false;

        int schemeEnd = fileSpan.IndexOf(FileIdParser.SchemeSeparator.AsSpan());
        if (schemeEnd == -1) return false;

        var afterSpan = fileSpan[(schemeEnd + FileIdParser.SchemeSeparator.Length)..];
        int basketEnd = afterSpan.IndexOf('/');
        if (basketEnd == -1) return false;

        var basketName = afterSpan[..basketEnd];

        return basketName.Equals(_partName, StringComparison.Ordinal);
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(fileStream);

        metadata.Validate();

        DateTimeOffset createdAtDay = _timeProvider.GetUtcNow().Date;
        long createdAt = createdAtDay.ToUnixTimeSeconds();


        await partManager.EnsureParts(
            _qualifiedTableName,
            createdAtDay,
            [metadata.TenantId, _partName],
            cancellationToken).ConfigureAwait(false);

        string fileId = FileIdParser.FormatToFileId(
            StorageType, _partName, metadata.TenantId, createdAtDay, metadata.FileName);

        string fileExtension = FileIdParser.GetFileExtension(metadata.FileName);

        // If stream isn't seekable, copy into a recyclable MemoryStream first
        Stream ms = fileStream;
        bool ownsMs = false;

        if (!fileStream.CanSeek)
        {
            ms = streamManager.GetStream();
            await fileStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ownsMs = true;
        }
        else
        {
            ms.Position = 0;
        }

        try
        {
            var sql = string.Format(InsertSql, _qualifiedTableName);

            await dataSource.ExecuteNonQuery(sql,
            [
                  new NpgsqlParameter<string>("id", fileId)
                , new NpgsqlParameter<string>("name", metadata.FileName)
                , new NpgsqlParameter<string>("file_ext", fileExtension)
                , new NpgsqlParameter<Stream>("data", ms)
                // 64-bit: the column is BIGINT, and the previous (int) cast silently truncated
                // the length of anything over 2 GB, recording a wrong size with no error.
                , new NpgsqlParameter<long>("size", ms.Length)
                , new NpgsqlParameter<int>("tenant_id", metadata.TenantId)
                , new NpgsqlParameter<string>("basket", _partName)
                , new NpgsqlParameter<long>("created_at", createdAt)
            ], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Only dispose the buffer we allocated — never dispose the caller's stream
            if (ownsMs)
            {
                await ms.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new StorageResult(fileId, fileId, StorageType, createdAtDay);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();


        if (!CanProcess(fileId))
            return false;


        if (!FileIdParser.TryParse(fileId, out _, out int tenantId, out long timestamp, out _))
        {
            return false;
        }

        var sql = string.Format(DeleteSql, _qualifiedTableName);


        int rowsAffected = await dataSource.ExecuteNonQuery(sql,
        [
            new NpgsqlParameter<int>("tenant_id", tenantId),
            new NpgsqlParameter<string>("basket", _partName),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ], cancellationToken).ConfigureAwait(false);

        return rowsAffected > 0;
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {


        if (!CanProcess(fileId))
            return false;

        if (!FileIdParser.TryParse(fileId, out _, out int tenantId, out long timestamp, out _))
        {
            return false;
        }

        var sql = string.Format(SelectSql, _qualifiedTableName);

        int rowsAffected = await dataSource.ExecuteReader(sql, async (reader, i) =>
        {
            using var fs = await reader.GetStreamAsync(0, cancellationToken).ConfigureAwait(false);
            await loadStream(fs, cancellationToken).ConfigureAwait(false);
        },
        [
            new NpgsqlParameter<int>("tenant_id", tenantId),
            new NpgsqlParameter<string>("basket", _partName),
            new NpgsqlParameter<long>("timestamp", timestamp),
            new NpgsqlParameter<string>("id", fileId)
        ], cancellationToken).ConfigureAwait(false);

        return rowsAffected > 0;
    }

    public Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (!CanProcess(fileId))
            return Task.FromResult<FileMetadata?>(null);

        if (!FileIdParser.TryParse(fileId, out _, out var tenantId, out _, out var fileName))
            return Task.FromResult<FileMetadata?>(null);

        var metadata = new FileMetadata
        {
            Basket = _partName,
            StorageType = StorageType,
            FileName = fileName,
            TenantId = tenantId
        };

        return Task.FromResult<FileMetadata?>(metadata);
    }
}
