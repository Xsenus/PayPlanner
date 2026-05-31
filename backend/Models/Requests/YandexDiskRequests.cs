namespace PayPlanner.Api.Models.Requests;

public sealed class UpdateYandexDiskSettingsRequest
{
    public string? OAuthToken { get; set; }
    public bool ClearToken { get; set; }
    public string? RootPath { get; set; }
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
