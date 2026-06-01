namespace PayPlanner.Api.Models.Responses;

public class S3StorageProviderPresetDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public bool ForcePathStyle { get; init; }
}

public class S3StorageConnectionDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string RootPrefix { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public bool IsConfigured { get; init; }
    public string? AccessKeyPreview { get; init; }
    public string? SecretKeyPreview { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public class S3StorageConnectionsOverviewDto
{
    public List<S3StorageProviderPresetDto> ProviderPresets { get; init; } = [];
    public List<S3StorageConnectionDto> Connections { get; init; } = [];
}

public class S3StorageConnectionTestStepDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class S3StorageConnectionTestDto
{
    public bool Success { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string TestKey { get; init; } = string.Empty;
    public DateTime CheckedAt { get; init; }
    public List<S3StorageConnectionTestStepDto> Steps { get; init; } = [];
}

public class S3StorageObjectDto
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    public DateTime? LastModified { get; init; }
}

public class S3StorageListResponseDto
{
    public S3StorageObjectDto Current { get; init; } = new();
    public List<S3StorageObjectDto> Items { get; init; } = [];
    public int Limit { get; init; }
    public int Offset { get; init; }
    public int Total { get; init; }
}

public class S3StorageOperationResultDto
{
    public bool Success { get; init; } = true;
    public string Path { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
}

public class S3StorageTextFileDto
{
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}
