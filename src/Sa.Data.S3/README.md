# Sa.Data.S3

This is a fork of https://github.com/teoadal/Storage , which is a wrapper around HttpClient for working with S3-compatible storage. It offers performance comparable to MinIO while consuming almost 200 times less memory than the AWS SDK client.

## Forked

- https://github.com/dundich/Storage;
- https://github.com/teoadal/Storage;


Это обертка над HttpClient для работы с S3 хранилищами. Мотивация создания была простейшей - я не понимал,
почему клиенты [AWS](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.0.0)
и [Minio](https://github.com/minio/minio-dotnet) (6.0.4) потребляют так много памяти. Результат экспериментов: скорость
почти как у AWS, а потребление памяти почти в 150 раз меньше, чем клиент для Minio (и в 17 для AWS).


## Creating a Client

To interact with an S3-compatible storage system, you need to create a client using the provided configuration. Below is a detailed explanation of the code snippet and its parameters.

```csharp

var client = new S3BucketClient(new HttpClient(), new S3BucketClientSettings
{
   Bucket = "mybucket",
   Endpoint = "http://localhost:9000",
   AccessKey = "ROOTUSER",
   SecretKey = "ChangeMe123"
};

```

## IS3BucketClient

```csharp

public interface IBucketOperations
{
	Task<bool> CreateBucket(CancellationToken ct);
	Task<bool> DeleteBucket(CancellationToken ct);
	Task<bool> IsBucketExists(CancellationToken ct);
}

public interface IFileOperations
{
	string BuildFileUrl(string fileName);
	string BuildFileUrl(string fileName, TimeSpan expiration);
	Task DeleteFile(string fileName, CancellationToken ct);
	Task<S3File> GetFile(string fileName, CancellationToken ct);
	Task<Stream> GetFileStream(string fileName, CancellationToken ct);
	Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct);
	Task<bool> IsFileExists(string fileName, CancellationToken ct);
	IAsyncEnumerable<string> List(string? prefix, CancellationToken ct);
	Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct);
	Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct);
	Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct);
}

public interface IS3BucketClient: IBucketOperations, IFileOperations
{
	string Bucket { get; }
	Uri Endpoint { get; }
}
```
