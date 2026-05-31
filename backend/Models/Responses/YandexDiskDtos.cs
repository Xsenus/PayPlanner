namespace PayPlanner.Api.Models.Responses;

public sealed class YandexDiskSettingsDto
{
    public bool IsConfigured { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string? TokenPreview { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class YandexDiskResourceDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public string FullPath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? Size { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
    public string? PublicUrl { get; set; }
}

public sealed class YandexDiskListResponseDto
{
    public YandexDiskResourceDto Current { get; set; } = new();
    public IReadOnlyList<YandexDiskResourceDto> Items { get; set; } = Array.Empty<YandexDiskResourceDto>();
    public int Limit { get; set; }
    public int Offset { get; set; }
    public int Total { get; set; }
}

public sealed class YandexDiskTextFileDto
{
    public string Path { get; set; } = "/";
    public string Content { get; set; } = string.Empty;
}

public sealed class YandexDiskOperationResultDto
{
    public bool Success { get; set; }
    public string Path { get; set; } = "/";
}

public sealed class YandexDiskCrmSyncSettingsDto
{
    public bool Enabled { get; set; }
    public bool SyncInvoices { get; set; }
    public bool SyncActs { get; set; }
    public bool SyncContracts { get; set; }
    public bool SyncCaseDocuments { get; set; }
    public bool SyncTaskFiles { get; set; }
    public bool OverwriteExisting { get; set; }
    public string ConflictResolutionMode { get; set; } = "yandexDiskMaster";
    public string ConflictResolutionModeText { get; set; } = string.Empty;
    public string FolderTemplate { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = string.Empty;
    public IReadOnlyList<string> AvailableTokens { get; set; } = Array.Empty<string>();
    public DateTime? UpdatedAt { get; set; }
}

public sealed class YandexDiskCrmSyncPreviewDto
{
    public bool SyncEnabled { get; set; }
    public bool OverwriteExisting { get; set; }
    public string ConflictResolutionMode { get; set; } = "yandexDiskMaster";
    public string ConflictResolutionModeText { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string FolderTemplate { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = string.Empty;
    public IReadOnlyList<YandexDiskCrmSyncPreviewItemDto> Items { get; set; } = Array.Empty<YandexDiskCrmSyncPreviewItemDto>();
}

public sealed class YandexDiskCrmSyncPreviewItemDto
{
    public string DocumentKind { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string OperationText { get; set; } = string.Empty;
}

public sealed class YandexDiskCrmSyncStatusReportDto
{
    public int PendingCount { get; set; }
    public int FailedCount { get; set; }
    public int SuccessCount { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public IReadOnlyList<YandexDiskCrmSyncStatusItemDto> RecentItems { get; set; } = Array.Empty<YandexDiskCrmSyncStatusItemDto>();
}

public sealed class YandexDiskCrmSyncStatusItemDto
{
    public string DocumentKind { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public int ObjectId { get; set; }
    public int? ParentObjectId { get; set; }
    public string? FileName { get; set; }
    public string? YandexDiskPath { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
