using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Provides extension methods for common file storage operations on <see cref="IHybridFileStorage"/>.
/// </summary>
public static class HybridFileStorageExtensions
{
    /// <summary>
    /// Copies a file from the local filesystem into the hybrid file storage.
    /// </summary>
    /// <param name="storage">The hybrid file storage instance.</param>
    /// <param name="filePath">The path to the local file to upload.</param>
    /// <param name="basket">The target basket (container) name.</param>
    /// <param name="input">Metadata about the file being uploaded.</param>
    /// <param name="bufferSize">The size of the buffer used when reading the file. Defaults to 81920 bytes.</param>
    /// <param name="ct">A cancellation token to cancel the operation if needed.</param>
    /// <returns>A <see cref="StorageResult"/> containing the result of the upload operation.</returns>
    public static async Task<StorageResult> CopyFromFileAsync(
        this IHybridFileStorage storage,
        string filePath,
        string basket,
        UploadFileInput input,
        int bufferSize = 81920,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(basket);
        ArgumentNullException.ThrowIfNull(input);

        await using var fs = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        return await storage.UploadAsync(
            basket: basket,
            input: input,
            fileStream: fs,
            cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a file from one storage scope (basket) to another within the hybrid file storage system.
    /// </summary>
    /// <param name="storage">The hybrid file storage instance.</param>
    /// <param name="fileId">The unique identifier of the source file.</param>
    /// <param name="basket">The target basket (container) name.</param>
    /// <param name="configure">An optional callback to customize the upload metadata based on the source file's metadata.</param>
    /// <param name="ct">A cancellation token to cancel the operation if needed.</param>
    /// <returns>A <see cref="StorageResult"/> containing the result of the upload operation in the target basket.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the source file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the file is already in the target scope.</exception>
    public static async Task<StorageResult> CopyToBasketAsync(
        this IHybridFileStorage storage,
        string fileId,
        string basket,
        Func<FileMetadata, UploadFileInput>? configure = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(fileId);
        ArgumentNullException.ThrowIfNull(basket);

        var metadata = await storage.GetMetadataAsync(fileId, ct)
            .ConfigureAwait(false);

        if (metadata is null)
        {
            ThrowSourceFileNotFound(fileId);
        }

        UploadFileInput uploadInput = configure?.Invoke(metadata) ?? new UploadFileInput
        {
            TenantId = metadata.TenantId,
            FileName = metadata.FileName
        };

        if (uploadInput.TenantId == metadata.TenantId
            && string.Equals(metadata.Basket, basket, StringComparison.Ordinal)
            && string.Equals(metadata.FileName, uploadInput.FileName, StringComparison.Ordinal))
        {
            ThrowFileAlreadyInTargetScope();
        }

        StorageResult result = default!;
        bool downloaded = await storage.DownloadAsync(
            fileId,
            async (sourceStream, downloadCt) =>
            {
                result = await storage.UploadAsync(
                    basket,
                    uploadInput,
                    sourceStream,
                    downloadCt)
                    .ConfigureAwait(false);
            },
            ct)
            .ConfigureAwait(false);

        if (!downloaded)
        {
            ThrowSourceFileNotFound(fileId);
        }

        return result;
    }

    /// <summary>
    /// Copies multiple files from various storage scopes to a target basket in parallel.
    /// </summary>
    /// <param name="storage">The hybrid file storage instance.</param>
    /// <param name="fileIds">The collection of file IDs to copy.</param>
    /// <param name="basket">The target basket (container) name.</param>
    /// <param name="configure">An optional callback to customize the upload metadata based on each source file's metadata.</param>
    /// <param name="options">Optional settings controlling parallelism, timeout, error handling, and progress reporting.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the entire batch operation.</param>
    /// <returns>A <see cref="BatchResult{T}"/> containing lists of succeeded results and failed errors.</returns>
    public static async Task<BatchResult<StorageResult>> CopyToScopeBatchAsync(
        this IHybridFileStorage storage,
        IEnumerable<string> fileIds,
        string basket,
        Func<FileMetadata, UploadFileInput>? configure = null,
        BatchOptions? options = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(basket);

        var opts = options ?? new BatchOptions();
        var fileList = fileIds as IList<string> ?? [.. fileIds];

        if (fileList.Count == 0)
        {
            return new BatchResult<StorageResult>
            {
                Succeeded = [],
                Failed = []
            };
        }

        var succeeded = new List<StorageResult>(fileList.Count);
        var failed = new List<BatchError>();
        var progress = opts.Progress;
        int completed = 0;

        // Объект для синхронизации доступа к общим коллекциям и счетчикам
        Lock lockObj = new ();

        async Task<StorageResult?> ProcessFileAsync(string fileId, int index, CancellationToken ct)
        {
            try
            {
                // Используем ct от делегата, а не внешний cancellationToken
                using var cts = opts.OperationTimeout > TimeSpan.Zero
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;

                cts?.CancelAfter(opts.OperationTimeout);

                var operationCt = cts?.Token ?? ct;

                return await storage.CopyToBasketAsync(
                    fileId,
                    basket,
                    configure,
                    ct: operationCt)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Блокировка для безопасного изменения списков и атомарного отчета о прогрессе
                lock (lockObj)
                {
                    failed.Add(new BatchError(fileId, ex, index));
                    completed++;

                    progress?.Report(new BatchOperationProgress(
                        fileList.Count,
                        completed,
                        succeeded.Count,
                        failed.Count,
                        fileId,
                        ex));
                }
                return null;
            }
        }

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = opts.MaxDegreeOfParallelism
        };

        await Parallel.ForEachAsync(
            fileList.Select((id, idx) => (id, idx)),
            parallelOptions,
            async (item, ct) =>
            {
                var (fileId, index) = item;

                bool hasFailed;
                lock (lockObj)
                {
                    hasFailed = failed.Count > 0;
                }

                if (!opts.ContinueOnError && hasFailed)
                {
                    return;
                }

                var result = await ProcessFileAsync(fileId, index, ct)
                    .ConfigureAwait(false);

                if (result is not null)
                {
                    lock (lockObj)
                    {
                        succeeded.Add(result);
                        completed++;

                        progress?.Report(new BatchOperationProgress(
                            fileList.Count,
                            completed,
                            succeeded.Count,
                            failed.Count,
                            fileId));
                    }
                }
            });

        return new BatchResult<StorageResult>
        {
            Succeeded = succeeded.AsReadOnly(),
            Failed = failed.AsReadOnly()
        };
    }

    [DoesNotReturn]
    static void ThrowFileAlreadyInTargetScope() =>
        throw new InvalidOperationException("File already in target scope");

    [DoesNotReturn]
    static void ThrowSourceFileNotFound(string fileId) =>
        throw new FileNotFoundException($"Source file not found: {fileId}");
}
