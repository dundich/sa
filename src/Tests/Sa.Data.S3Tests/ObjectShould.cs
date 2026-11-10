using Sa.Data.S3;
using Sa.Data.S3.Fixture;
using Sa.Fixture;
using System.Net;

namespace Sa.Data.S3Tests;


public class ObjectShould(S3BucketClientFixture fixture) : IClassFixture<S3BucketClientFixture>
{
    public const string StreamContentType = "application/octet-stream";

    protected IS3BucketClient Client => fixture.Sub;
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    protected S3BucketClient GetNotExistsBucketClient() => fixture.CreateClient(GetRandomFileNameWithoutExtension());


    [Fact]
    public async Task AbortMultipart()
    {
        var fileName = GetRandomFileName();
        var data = GetByteArray(50 * 1024 * 1024);

        using var uploader = await Client.UploadFile(fileName, StreamContentType, CancellationToken);

        var addResult = await uploader.AddPart(data, CancellationToken);
        Assert.True(addResult);

        var abortResult = await uploader.Abort(CancellationToken);
        Assert.True(abortResult);
    }

    [Fact]
    public async Task AllowParallelUploadMultipleFiles()
    {
        const int parallelization = 10;

        var file = GetRandomFileNameWithoutExtension();
        var tasks = new Task<bool>[parallelization];
        for (var i = 0; i < parallelization; i++)
        {
            var fileData = GetByteStream(12 * 1024 * 1024);
            var fileName = $"{file}-{i}";
            tasks[i] = Task.Run(
                async () =>
                {
                    await Client.UploadFile(fileName, StreamContentType, fileData, CancellationToken);
                    if (!await Client.IsFileExists(fileName, CancellationToken))
                    {
                        return false;
                    }

                    await Client.DeleteFile(fileName, CancellationToken);
                    return true;
                },
                CancellationToken);
        }

        await Task.WhenAll(tasks);

        foreach (var task in tasks)
        {
            Assert.True(task.IsCompletedSuccessfully);

            Assert.True(await task);

            task.Dispose();
        }
    }

    [Fact]
    public void BuildUrl()
    {
        var fileName = GetRandomFileName();
        var result = Client.BuildFileUrl(fileName, TimeSpan.FromSeconds(100));
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task BeExists()
    {
        var fileName = await CreateTestFile();
        var fileExistsResult = await Client.IsFileExists(fileName, CancellationToken);
        Assert.True(fileExistsResult);
        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task BeNotExists()
    {
        var fileExistsResult = await Client.IsFileExists(GetRandomFileName(), CancellationToken);
        Assert.False(fileExistsResult);
    }

    [Fact]
    public async Task DeleteFile()
    {
        var fileName = await CreateTestFile();
        await Client.DeleteFile(fileName, CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task DisposeFileStream()
    {
        var fileName = await CreateTestFile();
        using var fileGetResult = await Client.GetFile(fileName, CancellationToken);

        var fileStream = await fileGetResult.GetStream(CancellationToken);
        await fileStream.DisposeAsync();

        await DeleteTestFile(fileName);

        Assert.True(true);
    }

    [Fact]
    public async Task DisposeStorageFile()
    {
        var fileName = await CreateTestFile();
        using var fileGetResult = await Client.GetFile(fileName, CancellationToken);

        fileGetResult.Dispose();

        await DeleteTestFile(fileName);
        Assert.True(true);
    }

    [Fact]
    public async Task GetFileStream()
    {
        var fileName = await CreateTestFile();

        var fileStream = await Client.GetFileStream(fileName, CancellationToken);

        using var bufferStream = GetEmptyByteStream();
        await fileStream.CopyToAsync(bufferStream, CancellationToken);

        await EnsureFileSame(fileName, bufferStream.ToArray());
        await DeleteTestFile(fileName);

        Assert.True(true);
    }

    [Fact]
    public async Task GetFileUrl()
    {
        var fileName = await CreateTestFile();

        var url = await Client.GetFileUrl(fileName, TimeSpan.FromSeconds(600), CancellationToken);

        Assert.NotNull(url);

        using var response = await fixture.HttpClient.GetAsync(url, CancellationToken);

        await DeleteTestFile(fileName);

        Assert.True(response.IsSuccessStatusCode, response.ReasonPhrase);
    }

    [Fact]
    public async Task GetFileUrlWithCyrillicName()
    {
        var fileName = await CreateTestFile($"при(ве)+т_как23дела{Guid.NewGuid()}.pdf");

        var url = await Client.GetFileUrl(fileName, TimeSpan.FromSeconds(600), CancellationToken);

        using var response = await fixture.HttpClient.GetAsync(url, CancellationToken);

        await DeleteTestFile(fileName);

        Assert.True(response.IsSuccessStatusCode, response.ReasonPhrase);
    }

    [Fact]
    public async Task HasValidUploadInformation()
    {
        var fileName = GetRandomFileName();

        using var uploader = await Client.UploadFile(fileName, StreamContentType, CancellationToken);

        Assert.Equal(fileName, uploader.FileName);
        Assert.NotEmpty(uploader.UploadId);
        Assert.Equal(0, uploader.Written);

        var partData = new byte[] { 1, 2, 3 };
        await uploader.AddPart(partData, CancellationToken);

        Assert.Equal(partData.Length, uploader.Written);

        await uploader.Abort(CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task HasValidInformation()
    {
        const int length = 1 * 1024 * 1024;
        const string contentType = "video/mp4";

        var fileName = await CreateTestFile(contentType: contentType);
        using var fileGetResult = await Client.GetFile(fileName, CancellationToken);

        Assert.True((bool)fileGetResult);
        Assert.Equal(contentType, fileGetResult.ContentType);
        Assert.True(fileGetResult.Exists);
        Assert.Equal(length, fileGetResult.Length);
        Assert.Equal(HttpStatusCode.OK, fileGetResult.StatusCode);

        await DeleteTestFile(fileName);

        Assert.True(true);
    }

    [Fact]
    public async Task HasValidStreamInformation()
    {
        const int length = 1 * 1024 * 1024;
        var fileName = await CreateTestFile(size: length);
        using var fileGetResult = await Client.GetFile(fileName, CancellationToken);

        var fileStream = await fileGetResult.GetStream(CancellationToken);

        Assert.True(fileStream.CanRead);
        Assert.False(fileStream.CanSeek);
        Assert.False(fileStream.CanWrite);
        Assert.Equal(length, fileStream.Length);

        await fileStream.DisposeAsync();
        await fileStream.DisposeAsync();

        await DeleteTestFile(fileName);

        Assert.True(true);
    }

    [Fact]
    public async Task ListFiles()
    {
        const int count = 2;
        var noiseFiles = new List<string>();
        var expectedFileNames = new string[count];
        var prefix = GetRandomFileName();

        for (var i = 0; i < count; i++)
        {
            var fileName = $"{prefix}#{GetRandomFileName()}";
            expectedFileNames[i] = await CreateTestFile(fileName, size: 1024);
            noiseFiles.Add(await CreateTestFile());
        }

        var actualFileNames = new List<string>();
        await foreach (var file in Client.List(prefix, CancellationToken))
        {
            actualFileNames.Add(file);
        }

        foreach (var expectedFileName in expectedFileNames)
        {
            Assert.Contains(expectedFileName, actualFileNames);
        }

        foreach (var fileName in expectedFileNames.Concat(noiseFiles))
        {
            await DeleteTestFile(fileName);
        }

        Assert.True(true);
    }

    [Fact]
    public async Task PutByteArray()
    {
        var fileName = GetRandomFileName();
        var data = GetByteArray(15000);
        var filePutResult = await Client.UploadFile(fileName, StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data);
        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task PutBigByteArray()
    {
        var fileName = GetRandomFileName();
        var data = GetByteArray(50 * 1024 * 1024);
        var filePutResult = await Client.UploadFile(fileName, StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data);
        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task PutStream()
    {
        var fileName = GetRandomFileName();
        var data = GetByteStream(15000);
        var filePutResult = await Client.UploadFile(fileName, StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data);
        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task PutBigStream()
    {
        var fileName = GetRandomFileName();
        var data = GetByteStream(50 * 1024 * 1024);
        var filePutResult = await Client.UploadFile(fileName, StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data);
        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task ReadFileStream()
    {
        var fileName = await CreateTestFile();

        var buffer = new byte[1024];
        var file = await Client.GetFile(fileName, CancellationToken);
        var fileStream = await file.GetStream(CancellationToken);


        var read = await fileStream.ReadAsync(buffer.AsMemory(), CancellationToken);
        Assert.True(read > 0);
        read = await fileStream.ReadAsync(buffer.AsMemory(10, 20), CancellationToken);
        Assert.True(read > 0);


#pragma warning disable S6966 // Awaitable method should be used
        read = fileStream.Read(buffer, 10, 20);
#pragma warning restore S6966 // Awaitable method should be used
        Assert.True(read > 0);



        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task Upload()
    {
        var fileName = GetRandomFileName();
        using var data = GetByteStream(12 * 1024 * 1024); // 12 Mb
        var filePutResult = await Client.UploadFile(fileName, StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data.ToArray());
        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task UploadCyrillicName()
    {
        var fileName = $"при(ве)+т_как23дела{Guid.NewGuid()}.pdf";
        using var data = GetByteStream();
        var uploadResult = await Client.UploadFile(fileName, StreamContentType, data, CancellationToken);

        await DeleteTestFile(fileName);

        Assert.True(uploadResult);
    }

    [Fact]
    public async Task NotThrowIfFileAlreadyExists()
    {
        var fileName = await CreateTestFile();

        await Client.UploadFile(fileName, StreamContentType, GetByteStream(), CancellationToken);

        Assert.True(true);

        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task NotThrowIfFileExistsWithNotExistsBucket()
    {
        using var client = GetNotExistsBucketClient();
        await client.IsFileExists(GetRandomFileName(), CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task NotThrowIfFileGetUrlWithNotExistsBucket()
    {
        using var client = GetNotExistsBucketClient();
        var result = await client.GetFileUrl(GetRandomFileName(), TimeSpan.FromSeconds(100), CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task NotThrowIfFileGetWithNotExistsBucket()
    {
        using var client = GetNotExistsBucketClient();

        var result = await client.GetFile(GetRandomFileName(), CancellationToken);
        Assert.NotNull(result);
        Assert.False(result.Exists);
    }

    [Fact]
    public async Task MissingFileHasNoLengthOrContentType()
    {
        // Регрессия: GetFile освобождал ответ на 404, после чего Length/ContentType бросали
        // ObjectDisposedException, потому что оба читают response.Content.
        using var file = await Client.GetFile(GetRandomFileName(), CancellationToken);

        Assert.False(file.Exists);
        Assert.Null(file.Length);
        Assert.Null(file.ContentType);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }

    [Fact]
    public async Task UploadEmptyStream()
    {
        // Пустой поток — обычный PUT нулевой длины. Раньше он уходил в multipart, не мог завершить
        // нулевую загрузку и возвращал false.
        var fileName = GetRandomFileName();
        using var data = GetEmptyByteStream();

        Assert.True(await Client.UploadFile(fileName, StreamContentType, data, CancellationToken));

        using var file = await Client.GetFile(fileName, CancellationToken);
        Assert.True(file.Exists);
        Assert.Equal(0, file.Length);

        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task ListFolderPrefix()
    {
        // «/» в префиксе обязан кодироваться как %2F и в URL, и в каноническом query: иначе подпись
        // не сходится у любого листинга по «папке».
        var folder = GetRandomFileNameWithoutExtension();
        var fileName = $"{folder}/nested/{GetRandomFileName()}";

        Assert.True(await Client.UploadFile(fileName, StreamContentType, GetByteArray(128), CancellationToken));

        var listed = new List<string>();
        await foreach (var key in Client.List($"{folder}/", CancellationToken))
        {
            listed.Add(key);
        }

        Assert.Equal([fileName], listed);

        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task ListKeyWithXmlSpecialCharacters()
    {
        // Ключ с «&» приходит из S3 экранированным (&amp;). Если вернуть его не раскодировав,
        // ключ перестаёт совпадать с сохранённым объектом.
        var folder = GetRandomFileNameWithoutExtension();
        var fileName = $"{folder}/a&b<c>d\"e'f.txt";

        Assert.True(await Client.UploadFile(fileName, StreamContentType, GetByteArray(16), CancellationToken));

        var listed = new List<string>();
        await foreach (var key in Client.List($"{folder}/", CancellationToken))
        {
            listed.Add(key);
        }

        Assert.Equal([fileName], listed);

        await DeleteTestFile(fileName);
    }

    [Fact]
    public async Task AddPartBeyondInitialCapacity()
    {
        // 17-я часть перерастает стартовый массив из 16 элементов.
        using var uploader = await Client.UploadFile(GetRandomFileName(), StreamContentType, CancellationToken);

        for (var i = 0; i < 17; i++)
        {
            Assert.True(await uploader.AddPart([(byte)i], CancellationToken));
        }

        Assert.Equal(17, uploader.Written);

        await uploader.Abort(CancellationToken);
    }

    [Fact]
    public async Task NotThrowIfGetNotExistsFile()
    {
        var fileName = GetRandomFileName();
        await Client.GetFile(fileName, CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task NotThrowIfDeleteFileNotExists()
    {
        await Client.DeleteFile(GetRandomFileName(), CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task NotThrowIfGetFileStreamNotFound()
    {
        var fileName = GetRandomFileName();
        await Client.GetFileStream(fileName, CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task ThrowIfBucketNotExists()
    {
        using var client = GetNotExistsBucketClient();

        var fileArray = GetByteArray();
        var fileName = GetRandomFileName();
        var fileStream = GetByteStream();

        // Проверка, что при попытке удалить файл выбрасывается HttpRequestException
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.DeleteFile(fileName, CancellationToken);
        });

        // Проверка, что при попытке загрузить файл из потока выбрасывается HttpRequestException
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.UploadFile(fileName, StreamContentType, fileStream, CancellationToken);
        });

        // Проверка, что при попытке загрузить файл из массива байтов выбрасывается HttpRequestException
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.UploadFile(fileName, StreamContentType, fileArray, CancellationToken);
        });
    }

    [Fact]
    public async Task ThrowIfUploadDisposed()
    {
        var fileName = GetRandomFileName();

        var uploader = await Client.UploadFile(fileName, StreamContentType, CancellationToken);
        uploader.Dispose();


        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await uploader.Abort(CancellationToken);
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await uploader.AddPart([], 0, CancellationToken);
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await uploader.Complete(CancellationToken);
        });
    }

    [Fact]
    public async Task DisposeAsyncAbortsUnfinishedUpload()
    {
        var fileName = GetRandomFileName();

        var uploader = await Client.UploadFile(fileName, StreamContentType, CancellationToken);
        Assert.True(await uploader.AddPart([1, 2, 3], CancellationToken));

        // Асинхронное освобождение прерывает загрузку: на сервере не остаются неполные части.
        await uploader.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => uploader.Abort(CancellationToken));
    }

    [Fact]
    public async Task DisposeAsyncAfterCompleteDoesNotAbort()
    {
        var fileName = GetRandomFileName();
        var data = GetByteArray(64);

        await using (var uploader = await Client.UploadFile(fileName, StreamContentType, CancellationToken))
        {
            Assert.True(await uploader.AddPart(data, CancellationToken));
            Assert.True(await uploader.Complete(CancellationToken));
        }

        Assert.True(await Client.IsFileExists(fileName, CancellationToken));

        await DeleteTestFile(fileName);
    }

    [Theory]
    [InlineData("some/foo/audio.wav", 1024)]
    [InlineData("another/path/test.mp3", 2048)]
    public async Task UploadFileWithNestedPath(string nestedFileName, int dataSize)
    {
        var data = GetByteArray(dataSize);
        // Act
        var result = await Client.UploadFile(nestedFileName, StreamContentType, data, CancellationToken);
        Assert.True(result);

        // Assert
        var exists = await Client.IsFileExists(nestedFileName, CancellationToken);
        Assert.True(exists, "The object should exist in the S3 bucket");

        await DeleteTestFile(nestedFileName);
    }

    [Fact]
    public async Task UsesTheInjectedArrayPool()
    {
        // IArrayPool — публичный интерфейс, поэтому его реализация из конструктора должна
        // использоваться на всём пути (буферы загрузки и подписи), а не только существовать.
        var pool = new CountingArrayPool();
        var fileName = GetRandomFileName();

        using var client = new S3BucketClient(
            new HttpClient(),
            fixture.CreateSettings(fixture.Settings.BucketName),
            arrayPool: pool);

        using var data = GetByteStream(1024);

        Assert.True(await client.UploadFile(fileName, StreamContentType, data, CancellationToken));
        Assert.True(pool.Rents > 0);

        await client.DeleteFile(fileName, CancellationToken);
    }

    private sealed class CountingArrayPool : IArrayPool
    {
        public int Rents { get; private set; }

        public T[] Rent<T>(int minimumLength)
        {
            Rents++;
            return System.Buffers.ArrayPool<T>.Shared.Rent(minimumLength);
        }

        public void ReturnToPool<T>(T[] array, bool clear = false)
            => System.Buffers.ArrayPool<T>.Shared.Return(array, clear);
    }

    private async Task<string> CreateTestFile(
        string? fileName = null,
        string contentType = StreamContentType,
        int? size = null)
    {
        fileName ??= GetRandomFileName();
        using var data = GetByteStream(size ?? 1 * 1024 * 1024); // 1 Mb

        var uploadResult = await Client.UploadFile(fileName, contentType, data, CancellationToken);

        Assert.True(uploadResult);

        return fileName;
    }

    private async Task EnsureFileSame(string fileName, byte[] expectedBytes)
    {
        using var getFileResult = await Client.GetFile(fileName, CancellationToken);

        using var memoryStream = GetEmptyByteStream(getFileResult.Length);
        var stream = await getFileResult.GetStream(CancellationToken);
        await stream.CopyToAsync(memoryStream, CancellationToken);

        Assert.Equal([.. expectedBytes], memoryStream.ToArray());
    }

    private async Task EnsureFileSame(string fileName, MemoryStream expectedBytes)
    {
        expectedBytes.Seek(0, SeekOrigin.Begin);

        using var getFileResult = await Client.GetFile(fileName, CancellationToken);

        using var memoryStream = GetEmptyByteStream(getFileResult.Length);
        var stream = await getFileResult.GetStream(CancellationToken);
        await stream.CopyToAsync(memoryStream, CancellationToken);


        Assert.Equal(expectedBytes.ToArray(), memoryStream.ToArray());
    }

    private Task DeleteTestFile(string fileName) => Client.DeleteFile(fileName, CancellationToken);


    public static byte[] GetByteArray(int size = FixtureHelper.DefaultByteArraySize) => FixtureHelper.GetByteArray(size);

    public static string GetRandomFileName() => FixtureHelper.GetRandomFileName();

    public static string GetRandomFileNameWithoutExtension() => Path.GetFileNameWithoutExtension(GetRandomFileName());

    public static MemoryStream GetByteStream(int size = FixtureHelper.DefaultByteArraySize) => FixtureHelper.GetByteStream(size);

    public static MemoryStream GetEmptyByteStream(long? size = null) => FixtureHelper.GetEmptyByteStream(size);
}
