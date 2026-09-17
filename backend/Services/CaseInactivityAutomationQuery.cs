using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// Builds the single server-side projection used by case-inactivity preview,
/// diagnostics and the background scheduler. Keeping the classification here
/// prevents the UI counters from drifting away from actual task creation.
/// </summary>
public static class CaseInactivityAutomationQuery
{
    public const string AutomationTag = "автоконтроль";

    private static readonly string[] ActiveTaskStatuses =
    [
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval"
    ];

    public static IQueryable<CaseInactivityAutomationCaseRow> Build(
        PaymentContext context,
        HashSet<int> vacationUserIds,
        bool hasAvailableDefaultAssignee,
        DateTime inactivityCutoff,
        DateTime escalationCutoff,
        IQueryable<ClientCase>? source = null,
        bool includeCompletedTaskCount = false)
    {
        var cases = (source ?? context.ClientCases).AsNoTracking();
        var taskCounts = context.TaskJournalItems
            .Where(task => task.ClientCaseId.HasValue)
            .GroupBy(task => task.ClientCaseId)
            .Select(group => new CaseTaskCountStats
            {
                ClientCaseId = group.Key,
                ActiveTaskCount = group.Count(task => ActiveTaskStatuses.Contains(task.Status)),
                CompletedTaskCount = includeCompletedTaskCount
                    ? group.Count(task => task.Status == "done" || task.Status == "cancelled")
                    : 0,
            });

        var taskActivity = context.TaskJournalItems
            .IgnoreQueryFilters()
            .Where(task => task.ClientCaseId.HasValue)
            .GroupBy(task => task.ClientCaseId)
            .Select(group => new CaseTaskActivityStats
            {
                ClientCaseId = group.Key,
                LastTaskAt = group.Max(task =>
                    !task.TagsJson.Contains(AutomationTag)
                        ? (DateTime?)(task.UpdatedAt ?? task.CreatedAt)
                        : null),
                LastControlCycleEndedAt = context.UseObjectAuditModel
                    ? group.Max(task =>
                        task.TagsJson.Contains(AutomationTag) &&
                        ((task.Status == "done" || task.Status == "cancelled") ||
                         EF.Property<bool>(task, "IsDeleted"))
                            ? (DateTime?)(EF.Property<DateTime?>(task, "DeletedAt") ??
                              task.UpdatedAt ??
                              task.CreatedAt)
                            : (DateTime?)null)
                    : group.Max(task =>
                        task.TagsJson.Contains(AutomationTag) &&
                        (task.Status == "done" || task.Status == "cancelled")
                            ? (DateTime?)(task.UpdatedAt ?? task.CreatedAt)
                            : null),
            });

        var taskComments = context.TaskJournalComments
            .Where(comment =>
                comment.TaskJournalItem.ClientCaseId.HasValue &&
                !comment.TaskJournalItem.TagsJson.Contains(AutomationTag))
            .GroupBy(comment => comment.TaskJournalItem.ClientCaseId)
            .Select(group => new CaseActivityStats
            {
                ClientCaseId = group.Key,
                LastActivityAt = group.Max(comment => (DateTime?)(comment.UpdatedAt ?? comment.CreatedAt)),
            });

        var documents = context.ClientCaseDocuments
            .GroupBy(document => document.ClientCaseId)
            .Select(group => new CaseActivityStats
            {
                ClientCaseId = group.Key,
                LastActivityAt = group.Max(document => (DateTime?)document.UploadedAt),
            });

        var courtEvents = context.ClientCaseCourtEvents
            .GroupBy(courtEvent => courtEvent.ClientCaseId)
            .Select(group => new CaseActivityStats
            {
                ClientCaseId = group.Key,
                LastActivityAt = group.Max(courtEvent => (DateTime?)(courtEvent.UpdatedAt ?? courtEvent.CreatedAt)),
            });

        var signals =
            from clientCase in cases
            where
                (clientCase.ClientId.HasValue && clientCase.Client != null) ||
                (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity != null)
            join taskCount in taskCounts
                on (int?)clientCase.Id equals taskCount.ClientCaseId into taskCountGroup
            from taskCount in taskCountGroup.DefaultIfEmpty()
            join taskActivityItem in taskActivity
                on (int?)clientCase.Id equals taskActivityItem.ClientCaseId into taskActivityGroup
            from taskActivityItem in taskActivityGroup.DefaultIfEmpty()
            join taskComment in taskComments
                on (int?)clientCase.Id equals taskComment.ClientCaseId into taskCommentGroup
            from taskComment in taskCommentGroup.DefaultIfEmpty()
            join document in documents
                on (int?)clientCase.Id equals document.ClientCaseId into documentGroup
            from document in documentGroup.DefaultIfEmpty()
            join courtEvent in courtEvents
                on (int?)clientCase.Id equals courtEvent.ClientCaseId into courtEventGroup
            from courtEvent in courtEventGroup.DefaultIfEmpty()
            select new CaseInactivityAutomationCaseRow
            {
                Id = clientCase.Id,
                Title = clientCase.Title,
                Status = clientCase.Status,
                IsExcludedStatus = CaseInactivityControlCyclePolicy.ExcludedCaseStatuses.Contains(clientCase.Status),
                ClientId = clientCase.ClientId,
                ClientName = clientCase.Client != null ? clientCase.Client!.Name : string.Empty,
                LegalEntityId = clientCase.LegalEntityId,
                LegalEntityName = clientCase.LegalEntity != null
                    ? (clientCase.LegalEntity!.ShortName != string.Empty
                        ? clientCase.LegalEntity!.ShortName
                        : clientCase.LegalEntity!.FullName ?? string.Empty)
                    : string.Empty,
                ResponsibleUserId = clientCase.ResponsibleUserId ??
                    (clientCase.Client != null ? clientCase.Client!.ResponsibleUserId : null) ??
                    (clientCase.LegalEntity != null ? clientCase.LegalEntity!.ResponsibleUserId : null),
                ResponsibleName = clientCase.ResponsibleUser != null
                    ? clientCase.ResponsibleUser!.FullName
                    : clientCase.Client != null && clientCase.Client!.ResponsibleUser != null
                        ? clientCase.Client!.ResponsibleUser!.FullName
                        : clientCase.LegalEntity != null && clientCase.LegalEntity!.ResponsibleUser != null
                            ? clientCase.LegalEntity!.ResponsibleUser!.FullName
                            : string.Empty,
                IsResponsibleOnVacation = vacationUserIds.Contains(
                    (clientCase.ResponsibleUserId ??
                     (clientCase.Client != null ? clientCase.Client!.ResponsibleUserId : null) ??
                     (clientCase.LegalEntity != null ? clientCase.LegalEntity!.ResponsibleUserId : null)) ?? 0),
                HasAvailableAssignee = hasAvailableDefaultAssignee ||
                    (clientCase.ResponsibleUserId.HasValue
                        ? clientCase.ResponsibleUser != null &&
                          clientCase.ResponsibleUser!.IsActive &&
                          clientCase.ResponsibleUser!.IsApproved
                        : clientCase.Client != null && clientCase.Client!.ResponsibleUserId.HasValue
                            ? clientCase.Client!.ResponsibleUser != null &&
                              clientCase.Client!.ResponsibleUser!.IsActive &&
                              clientCase.Client!.ResponsibleUser!.IsApproved
                            : clientCase.LegalEntity != null &&
                              clientCase.LegalEntity!.ResponsibleUser != null &&
                              clientCase.LegalEntity!.ResponsibleUser!.IsActive &&
                              clientCase.LegalEntity!.ResponsibleUser!.IsApproved),
                CreatedAt = clientCase.CreatedAt,
                LastViewedAt = clientCase.LastViewedAt,
                ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
                InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
                InactivityRemindersDisabledReason = clientCase.InactivityRemindersDisabledReason,
                ActiveTaskCount = (int?)taskCount!.ActiveTaskCount ?? 0,
                CompletedTaskCount = (int?)taskCount!.CompletedTaskCount ?? 0,
                LastTaskAt = taskActivityItem!.LastTaskAt,
                LastTaskCommentAt = taskComment!.LastActivityAt,
                LastControlCycleEndedAt = taskActivityItem.LastControlCycleEndedAt,
                LastDocumentAt = document!.LastActivityAt,
                LastCourtEventAt = courtEvent!.LastActivityAt,
            };

        var withMeaningfulActivity = signals
            .Select(item => new
            {
                Item = item,
                LastMeaningfulActivityAt = item.LastTaskAt.HasValue && item.LastTaskAt.Value > item.CreatedAt
                    ? item.LastTaskAt.Value
                    : item.CreatedAt,
            })
            .Select(row => new
            {
                row.Item,
                LastMeaningfulActivityAt = row.Item.LastTaskCommentAt.HasValue && row.Item.LastTaskCommentAt.Value > row.LastMeaningfulActivityAt
                    ? row.Item.LastTaskCommentAt.Value
                    : row.LastMeaningfulActivityAt,
            })
            .Select(row => new
            {
                row.Item,
                LastMeaningfulActivityAt = row.Item.LastDocumentAt.HasValue && row.Item.LastDocumentAt.Value > row.LastMeaningfulActivityAt
                    ? row.Item.LastDocumentAt.Value
                    : row.LastMeaningfulActivityAt,
            })
            .Select(row => new
            {
                row.Item,
                LastMeaningfulActivityAt = row.Item.LastCourtEventAt.HasValue && row.Item.LastCourtEventAt.Value > row.LastMeaningfulActivityAt
                    ? row.Item.LastCourtEventAt.Value
                    : row.LastMeaningfulActivityAt,
            })
            .Select(row => new
            {
                row.Item,
                LastMeaningfulActivityAt = row.Item.LastViewedAt.HasValue && row.Item.LastViewedAt.Value > row.LastMeaningfulActivityAt
                    ? row.Item.LastViewedAt.Value
                    : row.LastMeaningfulActivityAt,
            })
            .Select(row => new
            {
                row.Item,
                LastMeaningfulActivityAt = row.Item.ActivityConfirmedAt.HasValue && row.Item.ActivityConfirmedAt.Value > row.LastMeaningfulActivityAt
                    ? row.Item.ActivityConfirmedAt.Value
                    : row.LastMeaningfulActivityAt,
            });

        var withControlAnchor = withMeaningfulActivity.Select(row => new
        {
            row.Item,
            row.LastMeaningfulActivityAt,
            LastControlAnchorAt = row.Item.LastControlCycleEndedAt.HasValue && row.Item.LastControlCycleEndedAt.Value > row.LastMeaningfulActivityAt
                ? row.Item.LastControlCycleEndedAt.Value
                : row.LastMeaningfulActivityAt,
        });

        return withControlAnchor.Select(row => new CaseInactivityAutomationCaseRow
        {
            Id = row.Item.Id,
            Title = row.Item.Title,
            Status = row.Item.Status,
            ClientId = row.Item.ClientId,
            ClientName = row.Item.ClientName,
            LegalEntityId = row.Item.LegalEntityId,
            LegalEntityName = row.Item.LegalEntityName,
            ResponsibleUserId = row.Item.ResponsibleUserId,
            ResponsibleName = row.Item.ResponsibleName,
            IsResponsibleOnVacation = row.Item.IsResponsibleOnVacation,
            HasAvailableAssignee = row.Item.HasAvailableAssignee,
            CreatedAt = row.Item.CreatedAt,
            LastViewedAt = row.Item.LastViewedAt,
            ActivityConfirmedAt = row.Item.ActivityConfirmedAt,
            InactivityRemindersEnabled = row.Item.InactivityRemindersEnabled,
            InactivityRemindersDisabledReason = row.Item.InactivityRemindersDisabledReason,
            ActiveTaskCount = row.Item.ActiveTaskCount,
            CompletedTaskCount = row.Item.CompletedTaskCount,
            LastTaskAt = row.Item.LastTaskAt,
            LastTaskCommentAt = row.Item.LastTaskCommentAt,
            LastControlCycleEndedAt = row.Item.LastControlCycleEndedAt,
            LastDocumentAt = row.Item.LastDocumentAt,
            LastCourtEventAt = row.Item.LastCourtEventAt,
            LastMeaningfulActivityAt = row.LastMeaningfulActivityAt,
            LastControlAnchorAt = row.LastControlAnchorAt,
            IsExcludedStatus = row.Item.IsExcludedStatus,
            IsPausedByVacation = !row.Item.IsExcludedStatus && row.Item.IsResponsibleOnVacation,
            IsDisabled = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                !row.Item.InactivityRemindersEnabled,
            IsBlockedByActiveTask = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                row.Item.InactivityRemindersEnabled &&
                row.Item.ActiveTaskCount > 0,
            IsUnassigned = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                row.Item.InactivityRemindersEnabled &&
                row.Item.ActiveTaskCount == 0 &&
                !row.Item.HasAvailableAssignee,
            IsDueWithoutAssignee = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                row.Item.InactivityRemindersEnabled &&
                row.Item.ActiveTaskCount == 0 &&
                !row.Item.HasAvailableAssignee &&
                row.LastControlAnchorAt <= inactivityCutoff,
            IsRecentlyActive = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                row.Item.InactivityRemindersEnabled &&
                row.Item.ActiveTaskCount == 0 &&
                row.Item.HasAvailableAssignee &&
                row.LastControlAnchorAt > inactivityCutoff,
            IsCandidate = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                row.Item.InactivityRemindersEnabled &&
                row.Item.ActiveTaskCount == 0 &&
                row.Item.HasAvailableAssignee &&
                row.LastControlAnchorAt <= inactivityCutoff,
            IsCriticalCandidate = !row.Item.IsExcludedStatus &&
                !row.Item.IsResponsibleOnVacation &&
                row.Item.InactivityRemindersEnabled &&
                row.Item.ActiveTaskCount == 0 &&
                row.Item.HasAvailableAssignee &&
                row.LastMeaningfulActivityAt <= escalationCutoff &&
                (row.Item.LastControlCycleEndedAt ?? row.Item.CreatedAt) <= inactivityCutoff,
        });
    }
}

public sealed class CaseInactivityAutomationCaseRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public ClientCaseStatus Status { get; set; }
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int? LegalEntityId { get; set; }
    public string LegalEntityName { get; set; } = string.Empty;
    public int? ResponsibleUserId { get; set; }
    public string ResponsibleName { get; set; } = string.Empty;
    public bool IsResponsibleOnVacation { get; set; }
    public bool HasAvailableAssignee { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastViewedAt { get; set; }
    public DateTime? ActivityConfirmedAt { get; set; }
    public bool InactivityRemindersEnabled { get; set; }
    public string InactivityRemindersDisabledReason { get; set; } = string.Empty;
    public int ActiveTaskCount { get; set; }
    public int CompletedTaskCount { get; set; }
    public DateTime? LastTaskAt { get; set; }
    public DateTime? LastTaskCommentAt { get; set; }
    public DateTime? LastControlCycleEndedAt { get; set; }
    public DateTime? LastDocumentAt { get; set; }
    public DateTime? LastCourtEventAt { get; set; }
    public DateTime LastMeaningfulActivityAt { get; set; }
    public DateTime LastControlAnchorAt { get; set; }
    public bool IsExcludedStatus { get; set; }
    public bool IsPausedByVacation { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsBlockedByActiveTask { get; set; }
    public bool IsUnassigned { get; set; }
    public bool IsDueWithoutAssignee { get; set; }
    public bool IsRecentlyActive { get; set; }
    public bool IsCandidate { get; set; }
    public bool IsCriticalCandidate { get; set; }
}

internal sealed class CaseTaskCountStats
{
    public int? ClientCaseId { get; set; }
    public int ActiveTaskCount { get; set; }
    public int CompletedTaskCount { get; set; }
}

internal sealed class CaseTaskActivityStats
{
    public int? ClientCaseId { get; set; }
    public DateTime? LastTaskAt { get; set; }
    public DateTime? LastControlCycleEndedAt { get; set; }
}

internal sealed class CaseActivityStats
{
    public int? ClientCaseId { get; set; }
    public DateTime? LastActivityAt { get; set; }
}
