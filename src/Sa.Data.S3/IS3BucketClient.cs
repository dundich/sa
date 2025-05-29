namespace Sa.Data.S3;

public interface IS3BucketClient: IBucketOperations, IFileOperations
{
	string Bucket { get; }
	Uri Endpoint { get; }
}
