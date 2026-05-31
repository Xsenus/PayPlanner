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
