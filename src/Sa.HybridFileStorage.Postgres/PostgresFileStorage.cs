using Microsoft.IO;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
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
           size = EXCLUDED.size
        """;

    // `created_at` is matched by equality: it is the value embedded in the file ID, rows only ever
    // hold the UTC-midnight stamp of their upload day, and the primary key includes it. A `>=`
    // bound could only ever match that same value, while reading as if several rows were eligible.
    private const string DeleteSql =
        """
        DELETE FROM {0}
        WHERE tenant_id = @tenant_id AND basket = @basket
          AND created_at = @timestamp AND id = @id
        """;

    private const string SelectSql =
        """
        SELECT data FROM {0}
        WHERE tenant_id = @tenant_id AND basket = @basket
          AND created_at = @timestamp AND id = @id
        """;

    private const string SelectMetadataSql =
        """
        SELECT name, tenant_id FROM {0}
        WHERE tenant_id = @tenant_id AND basket = @basket
          AND created_at = @timestamp AND id = @id
        """;

    // The options reach this type only via the registration extension, which calls
    // PostgresFileStorageOptions.Validate() first. That guarantees Basket, TableName and
    // StorageType are already bare words and SchemaName is a single identifier, so they are
    // used verbatim. Rewriting them here (the previous Sanitize) made the DDL registered under
    // the raw name and the queries under the rewritten one, and made StorageType disagree with
    // _schemePrefix — a file ID this storage produced could not be processed by it.
    private readonly string _partName = options.Basket;

    // The qualified name and the four statements are fixed for the lifetime of the instance, so they
    // are built once. (Field initializers cannot read another field, hence the static helper.)
    private readonly string _qualifiedTableName = Qualify(options);
    private readonly string _insertSql = string.Format(InsertSql, Qualify(options));
    private readonly string _deleteSql = string.Format(DeleteSql, Qualify(options));
    private readonly string _selectSql = string.Format(SelectSql, Qualify(options));
    private readonly string _selectMetadataSql = string.Format(SelectMetadataSql, Qualify(options));

    private readonly string _schemePrefix
        = $"{options.StorageType}{FileIdParser.SchemeSeparator}{options.Basket}/";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private static string Qualify(PostgresFileStorageOptions options)
        => $"\"{options.SchemaName ?? PostgresFileStorageSchema.FallbackSchema}\".\"{options.TableName}\"";

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
        // The prefix already carries the basket (`pg://share/`), so a storage only claims file IDs
        // of its own scheme *and* basket — two registrations over one scheme never fight over an ID.
        // Anything else (another scheme, a sibling basket such as `share2`, an empty string) is not
        // this storage's, and the hybrid chain keeps probing.
        return !string.IsNullOrEmpty(fileId)
            && fileId.AsSpan().StartsWith(_schemePrefix.AsSpan(), StringComparison.Ordinal);
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        // The argument check runs before the read-only guard: a caller that passed no stream should
        // learn about the bad argument, not about the storage's mode.
        ArgumentNullException.ThrowIfNull(fileStream);
        EnsureWritable();

        metadata.Validate();

        // The column widths are part of this provider's contract, so an over-long name is rejected
        // here with a named argument error instead of surfacing as a bare PostgreSQL 22001 on the
        // insert. Length counts UTF-16 units, i.e. never fewer than the characters varchar() counts.
        if (metadata.FileName.Length > PostgresFileStorageTable.NameMaxLength)
        {
            throw new ArgumentException(
                $"FileName exceeds the maximum length of {PostgresFileStorageTable.NameMaxLength} " +
                $"characters, but was {metadata.FileName.Length}.",
                nameof(UploadFileInput.FileName));
        }

        // PostgreSQL text cannot hold a NUL byte, and the name reaches both the `name` column and the
        // file ID, so a NUL is rejected here instead of failing the insert with a bare 22021.
        if (metadata.FileName.Contains('\0'))
        {
            throw new ArgumentException(
                "FileName must not contain a NUL character.", nameof(UploadFileInput.FileName));
        }

        // A seekable stream knows its size up front, so an oversized upload is rejected before any
        // partition DDL runs. A non-seekable one is measured while it is buffered (see below).
        long maxBytes = options.MaxUploadSizeBytes;

        if (maxBytes > 0 && fileStream.CanSeek && fileStream.Length > maxBytes)
        {
            ThrowUploadTooLarge(fileStream.Length, maxBytes);
        }

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

        if (fileExtension.Length > PostgresFileStorageTable.FileExtMaxLength)
        {
            throw new ArgumentException(
                $"The file extension exceeds the maximum length of " +
                $"{PostgresFileStorageTable.FileExtMaxLength} characters, but was {fileExtension.Length}.",
                nameof(UploadFileInput.FileName));
        }

        // If stream isn't seekable, copy into a recyclable MemoryStream first. Npgsql writes a
        // Stream parameter from its *current* position, so the buffer has to be rewound before it
        // is handed over — otherwise the copy (left at the end) was stored as an empty `data`.
        // The whole block sits in the try: a failure while buffering (the size guard, a read error)
        // must still return the buffer to the manager.
        Stream ms = fileStream;
        bool ownsMs = false;

        try
        {
            if (!fileStream.CanSeek)
            {
                ms = streamManager.GetStream();
                ownsMs = true;

                await CopyToBufferAsync(fileStream, ms, maxBytes, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
            }
            else
            {
                ms.Position = 0;
            }

            await dataSource.ExecuteNonQuery(_insertSql,
            [
                  new NpgsqlParameter<string>("id", fileId)
                , new NpgsqlParameter<string>("name", metadata.FileName)
                , new NpgsqlParameter<string>("file_ext", fileExtension)
                , new NpgsqlParameter<Stream>("data", ms)
                // 64-bit end to end: the column is BIGINT and the parameter is long, so the recorded
                // size can never be silently truncated. (PostgreSQL caps a single bytea at 1 GB,
                // well below int.MaxValue, but the type must still match the declared column.)
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

        return new StorageResult(fileId, fileId, StorageType, _timeProvider.GetUtcNow());
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        EnsureWritable();


        if (!CanProcess(fileId))
            return false;


        if (!FileIdParser.TryParse(fileId, out _, out int tenantId, out long timestamp, out _))
        {
            return false;
        }

        int rowsAffected;

        try
        {
            rowsAffected = await dataSource.ExecuteNonQuery(_deleteSql,
            [
                new NpgsqlParameter<int>("tenant_id", tenantId),
                new NpgsqlParameter<string>("basket", _partName),
                new NpgsqlParameter<long>("timestamp", timestamp),
                new NpgsqlParameter<string>("id", fileId)
            ], cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsMissingTable(ex))
        {
            // Nothing has been written here yet (no upload, no migration schedule), so there is
            // nothing to delete — the same answer as a missing row.
            return false;
        }

        return rowsAffected > 0;
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        ArgumentNullException.ThrowIfNull(loadStream);


        if (!CanProcess(fileId))
            return false;

        if (!FileIdParser.TryParse(fileId, out _, out int tenantId, out long timestamp, out _))
        {
            return false;
        }

        // The connection is opened directly instead of going through
        // IPgDataSource.ExecuteReader: that takes a synchronous Action, so an `async` callback
        // there compiles to `async void` — DownloadAsync would return before the consumer finished
        // and a failure inside its callback would be unobservable. A row's bytea can only be
        // streamed while its reader is open, so the whole read has to be awaited in one place.
        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnection(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand cmd = new(_selectSql, connection);

        cmd.Parameters.Add(new NpgsqlParameter<int>("tenant_id", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter<string>("basket", _partName));
        cmd.Parameters.Add(new NpgsqlParameter<long>("timestamp", timestamp));
        cmd.Parameters.Add(new NpgsqlParameter<string>("id", fileId));

        NpgsqlDataReader reader;

        try
        {
            reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsMissingTable(ex))
        {
            // Nothing has been written here yet (no upload, no migration schedule): for a read, a
            // table that does not exist is the same as a missing row.
            return false;
        }

        await using (reader)
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await using Stream stream = await reader
                .GetStreamAsync(0, cancellationToken).ConfigureAwait(false);

            await loadStream(stream, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId);

        if (!CanProcess(fileId))
            return null;

        if (!FileIdParser.TryParse(fileId, out _, out var tenantId, out var timestamp, out _))
            return null;

        // A well-formed file ID is not a file — the row is. Parsing alone reported metadata for a
        // deleted or never-uploaded file, and the hybrid chain reads `null` as "not here" and keeps
        // probing, so a fabricated answer would make this storage win the chain.
        List<FileMetadata> rows;

        try
        {
            rows = await dataSource.ExecuteReaderList(_selectMetadataSql,
                reader => new FileMetadata
                {
                    Basket = _partName,
                    StorageType = StorageType,
                    FileName = reader.GetString(0),
                    TenantId = reader.GetInt32(1)
                },
                [
                    new NpgsqlParameter<int>("tenant_id", tenantId),
                    new NpgsqlParameter<string>("basket", _partName),
                    new NpgsqlParameter<long>("timestamp", timestamp),
                    new NpgsqlParameter<string>("id", fileId)
                ], cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsMissingTable(ex))
        {
            // Nothing has been written here yet (no upload, no migration schedule): for a read, a
            // table that does not exist is the same as a missing row.
            return null;
        }

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Whether the server rejected a statement because the table (or its schema) does not exist.
    /// </summary>
    /// <remarks>
    /// The table is created by the first upload (<c>EnsureParts</c>) or by the migration schedule,
    /// so a read that runs before either is a plain "not found" — not an error. This mirrors how the
    /// partitional cache treats the same codes, and keeps the hybrid chain from seeing an exception
    /// where the honest answer is "no such file".
    /// </remarks>
    private static bool IsMissingTable(PostgresException ex)
        => ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName;

    /// <summary>
    /// Copies a non-seekable source into the buffer, enforcing <paramref name="maxBytes"/> while it
    /// reads: the size of such a stream is unknown up front, and buffering it unbounded is an
    /// unbounded allocation.
    /// </summary>
    private static async Task CopyToBufferAsync(
        Stream source,
        Stream target,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            long total = 0;
            int read;

            while ((read = await source
                       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;

                if (maxBytes > 0 && total > maxBytes)
                {
                    ThrowUploadTooLarge(total, maxBytes);
                }

                await target
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [DoesNotReturn]
    private static void ThrowUploadTooLarge(long size, long maxBytes)
        => throw new InvalidOperationException(
            $"The upload is at least {size} bytes, which exceeds the configured " +
            $"{nameof(PostgresFileStorageOptions.MaxUploadSizeBytes)} ({maxBytes}).");
}
