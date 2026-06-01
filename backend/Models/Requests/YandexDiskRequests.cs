namespace PayPlanner.Api.Models.Requests;

public sealed class UpdateYandexDiskSettingsRequest
{
    public string? OAuthToken { get; set; }
    public bool ClearToken { get; set; }
    public string? RootPath { get; set; }
}

public sealed class TestYandexDiskConnectionRequest
{
    public string? TestFolderPath { get; set; }
}

public sealed class CreateYandexDiskFolderRequest
{
    public string? Path { get; set; }
}

public sealed class YandexDiskResourceOperationRequest
{
    public string? FromPath { get; set; }
    public string? ToPath { get; set; }
    public bool Overwrite { get; set; }
}

public sealed class UpdateYandexDiskTextFileRequest
{
    public string? Path { get; set; }
    public string? Content { get; set; }
    public bool Overwrite { get; set; } = true;
}

public sealed class UpdateYandexDiskCrmSyncSettingsRequest
{
    public bool Enabled { get; set; }
    public bool SyncInvoices { get; set; }
    public bool SyncActs { get; set; }
    public bool SyncContracts { get; set; }
    public bool SyncCaseDocuments { get; set; }
    public bool SyncTaskFiles { get; set; }
    public bool OverwriteExisting { get; set; }
    public string? ConflictResolutionMode { get; set; }
    public string? FolderTemplate { get; set; }
    public string? FileNameTemplate { get; set; }
}
