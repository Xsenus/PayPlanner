using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

/// <summary>
/// Сохраняемое поручение на фоновую передачу актуального состояния задачи
/// в подключённые внешние календари.
/// </summary>
public sealed class ExternalCalendarTaskSyncRequest
{
    public long Id { get; set; }

    public int TaskJournalItemId { get; set; }

    [Required]
    [MaxLength(32)]
    public string SourceProvider { get; set; } = "crm";

    public int AttemptCount { get; set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = ExternalCalendarTaskSyncRequestStatuses.Queued;

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Состояния поручения фоновой синхронизации задачи.
/// </summary>
public static class ExternalCalendarTaskSyncRequestStatuses
{
    public const string Queued = "queued";
    public const string Failed = "failed";
}
