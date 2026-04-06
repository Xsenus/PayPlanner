namespace PayPlanner.Api.Models.Responses;

public class DatabaseBackupFileDto
{
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Trigger { get; init; } = string.Empty;
}

public class DatabaseBackupFormatOptionDto
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string FileExtension { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

public class DatabaseBackupOverviewDto
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderDisplayName { get; init; } = string.Empty;
    public string BackupFormat { get; init; } = string.Empty;
    public string FileExtension { get; init; } = string.Empty;
    public string SelectedFormat { get; init; } = string.Empty;
    public List<DatabaseBackupFormatOptionDto> AvailableFormats { get; init; } = [];
    public bool AutomaticEnabled { get; init; }
    public string ScheduleLocal { get; init; } = string.Empty;
    public int RetentionDays { get; init; }
    public string RootDirectory { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public string? ToolCommand { get; init; }
    public List<DatabaseBackupFileDto> Items { get; init; } = [];
}

public class DatabaseBackupCreateResultDto
{
    public DatabaseBackupFileDto Backup { get; init; } = new();
    public string DownloadPath { get; init; } = string.Empty;
}

public class DatabaseBackupDeleteResultDto
{
    public int DeletedCount { get; init; }
    public bool DeletedAll { get; init; }
    public DatabaseBackupOverviewDto Overview { get; init; } = new();
}
