namespace PayPlanner.Api.Models.Requests;

public class UpsertS3StorageConnectionRequest
{
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Provider { get; init; } = "hostkey";
    public string Endpoint { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string RootPrefix { get; init; } = string.Empty;
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;
    public bool ClearSecret { get; init; }
    public bool IsEnabled { get; init; } = true;
}

public class TestS3StorageConnectionRequest
{
    public string TestPrefix { get; init; } = "_payplanner-test";
}

public class CreateS3StorageFolderRequest
{
    public string Path { get; init; } = string.Empty;
}

public class S3StorageObjectOperationRequest
{
    public string FromPath { get; init; } = string.Empty;
    public string ToPath { get; init; } = string.Empty;
    public bool Overwrite { get; init; }
}

public class UpdateS3StorageTextFileRequest
{
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool Overwrite { get; init; } = true;
}
