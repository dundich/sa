using Sa.HybridFileStorage.Domain;
using System.Diagnostics.CodeAnalysis;

namespace Sa.HybridFileStorage;

public static class HybridFileStorageExtensions
{
    public static async Task<StorageResult> CopyFromFileAsync(
        this IHybridFileStorage storage,
        string filePath,
        string basket,
        UploadFileInput input,
        int bufferSize = 81920,
        CancellationToken ct = default)
    {
        // копируем файл в хранилище
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
            cancellationToken: ct);
    }

    public static async Task<StorageResult> CopyToBasketAsync(
        this IHybridFileStorage storage,
        string fileId,
        string basket,
        Func<FileMetadata, UploadFileInput>? configure = null,
        CancellationToken ct = default)
    {
        var metadata = await storage.GetMetadataAsync(fileId, ct);

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
                    downloadCt);
            },
            ct);

        if (!downloaded)
        {
            ThrowSourceFileNotFound(fileId);
        }

        return result;
    }


    [DoesNotReturn]
    static void ThrowFileAlreadyInTargetScope() =>
        throw new InvalidOperationException("File already in target scope");

    [DoesNotReturn]
    static void ThrowSourceFileNotFound(string fileId) =>
        throw new FileNotFoundException($"Source file not found: {fileId}");


    public static async Task<BatchResult<StorageResult>> CopyToScopeBatchAsync(
        this IHybridFileStorage storage,
        IEnumerable<string> fileIds,
        string basket,
        Func<FileMetadata, UploadFileInput>? configure = null,
        BatchOptions? options = default,
        CancellationToken cancellationToken = default)
    {

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


        async Task<StorageResult?> ProcessFileAsync(string fileId, int index)
        {
            try
            {
                using var cts = opts.OperationTimeout > TimeSpan.Zero
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;

                cts?.CancelAfter(opts.OperationTimeout);

                var ct = cts?.Token ?? cancellationToken;

                return await storage.CopyToBasketAsync(
                    fileId,
                    basket,
                    configure,
                    ct: ct);
            }
            catch (Exception ex)
            {
                failed.Add(new BatchError(fileId, ex, index));
                progress?.Report(new BatchOperationProgress(
                    fileList.Count,
                    Interlocked.Increment(ref completed),
                    succeeded.Count,
                    failed.Count,
                    fileId,
                    ex));
                return null;
            }
        }


        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = opts.MaxDegreeOfParallelism
        };

        await Parallel.ForEachAsync(fileList.Select((id, idx) => (id, idx)), parallelOptions, async (item, ct) =>
        {
            var (fileId, index) = item;

            if (!opts.ContinueOnError && failed.Count > 0)
            {
                return;
            }

            var result = await ProcessFileAsync(fileId, index);

            if (result is not null)
            {
                lock (succeeded)
                {
                    succeeded.Add(result);
                }
            }

            if (result is not null)
            {
                progress?.Report(new BatchOperationProgress(
                    fileList.Count,
                    Interlocked.Increment(ref completed),
                    succeeded.Count,
                    failed.Count,
                    fileId));
            }
        });

        return new BatchResult<StorageResult>
        {
            Succeeded = succeeded.AsReadOnly(),
            Failed = failed.AsReadOnly()
        };
    }
}
