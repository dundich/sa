using Sa.HybridFileStorage.Domain;
using System.Diagnostics.CodeAnalysis;

namespace Sa.HybridFileStorage;

public static class HybridFileStorageExtensions
{
    public static async Task<StorageResult> CopyToScopeAsync(
        this IHybridFileStorage storage,
        string fileId,
        string sourceScopeName,
        string targetScopeName,
        int? targetTenantId = default,
        CancellationToken ct = default)
    {
        var metadata = await storage.GetMetadataAsync(fileId, sourceScopeName, ct);

        if (metadata is null)
        {
            ThrowSourceFileNotFound(fileId);
        }

        int tenantId = targetTenantId ?? metadata.TenantId;

        if (tenantId == metadata.TenantId
            && string.Equals(sourceScopeName, targetScopeName, StringComparison.Ordinal))
        {
            ThrowFileAlreadyInTargetScope();
        }

        var uploadInput = new UploadFileInput
        {
            FileName = metadata.FileName,
            TenantId = tenantId,
        };

        StorageResult result = default!;
        bool downloaded = await storage.DownloadAsync(
            fileId,
            sourceScopeName,
            async (sourceStream, downloadCt) =>
            {
                result = await storage.UploadAsync(
                    uploadInput,
                    targetScopeName,
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
        string sourceScopeName,
        string targetScopeName,
        int? targetTenantId = default,
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

                return await storage.CopyToScopeAsync(fileId, sourceScopeName, targetScopeName, targetTenantId, ct);
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
