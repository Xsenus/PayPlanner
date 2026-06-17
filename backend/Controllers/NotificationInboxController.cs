using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/notifications/inbox")]
[Authorize]
public class NotificationInboxController : ControllerBase
{
    private readonly PaymentContext _context;

    public NotificationInboxController(PaymentContext context)
    {
        _context = context;
    }

    [HttpGet("types")]
    public ActionResult<IReadOnlyList<UserNotificationTypeDto>> GetTypes()
        => Ok(UserNotificationTypes.All.Select(MapType).ToList());

    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var visibility = await ResolveNotificationTargetVisibilityAsync(ct);
        var disabledTypes = await LoadDisabledNotificationTypesAsync(userId.Value, ct);
        var unreadQuery = _context.UserNotifications
            .AsNoTracking()
            .Where(item =>
                item.RecipientUserId == userId.Value
                && !item.IsRead
                && !item.IsArchived
                && item.DeletedAt == null);
        unreadQuery = ApplyDisabledTypeFilter(unreadQuery, disabledTypes);
        var count = await WhereNotificationTargetVisible(unreadQuery, _context, visibility)
            .CountAsync(ct);

        return Ok(count);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<UserNotificationDto>>> GetNotifications(
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] string? search,
        [FromQuery] string? dateFrom = null,
        [FromQuery] string? dateTo = null,
        [FromQuery] int? employeeId = null,
        [FromQuery] int? clientId = null,
        [FromQuery] string? taskRole = null,
        [FromQuery] string? domain = null,
        [FromQuery] bool? requiresAction = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.UserNotifications
            .AsNoTracking()
            .Where(item => item.RecipientUserId == userId.Value && item.DeletedAt == null);
        var visibility = await ResolveNotificationTargetVisibilityAsync(ct);
        var disabledTypes = await LoadDisabledNotificationTypesAsync(userId.Value, ct);

        query = ApplyStatusFilter(query, status);
        query = ApplyDisabledTypeFilter(query, disabledTypes);
        query = ApplyTypeAndSearchFilters(query, type, search);
        query = ApplyDomainFilter(query, domain);
        query = ApplyRequiresActionFilter(query, requiresAction);
        query = ApplyDateRangeFilter(query, dateFrom, dateTo);
        query = ApplyTaskReferenceFilters(query, _context, employeeId, clientId, taskRole);
        query = WhereNotificationTargetVisible(query, _context, visibility);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(item => item.IsRead)
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<UserNotificationDto>
        {
            Items = items.Select(Map).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<UserNotificationBulkActionResultDto>> MarkAllRead(
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] string? search,
        [FromQuery] string? dateFrom = null,
        [FromQuery] string? dateTo = null,
        [FromQuery] int? employeeId = null,
        [FromQuery] int? clientId = null,
        [FromQuery] string? taskRole = null,
        [FromQuery] string? domain = null,
        [FromQuery] bool? requiresAction = null,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var query = _context.UserNotifications
            .Where(item => item.RecipientUserId == userId.Value && item.DeletedAt == null);
        var visibility = await ResolveNotificationTargetVisibilityAsync(ct);
        var disabledTypes = await LoadDisabledNotificationTypesAsync(userId.Value, ct);

        query = ApplyStatusFilter(query, status);
        query = ApplyDisabledTypeFilter(query, disabledTypes);
        query = ApplyTypeAndSearchFilters(query, type, search);
        query = ApplyDomainFilter(query, domain);
        query = ApplyRequiresActionFilter(query, requiresAction);
        query = ApplyDateRangeFilter(query, dateFrom, dateTo);
        query = ApplyTaskReferenceFilters(query, _context, employeeId, clientId, taskRole);
        query = WhereNotificationTargetVisible(query, _context, visibility);

        var items = await query
            .Where(item => !item.IsRead)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            MarkAsRead(item);
        }

        if (items.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return Ok(new UserNotificationBulkActionResultDto { UpdatedCount = items.Count });
    }

    [HttpPost("bulk/archive")]
    public async Task<ActionResult<UserNotificationBulkActionResultDto>> ArchiveSelected([FromBody] BulkIdsRequest request, CancellationToken ct)
    {
        var items = await FindOwnedNotificationsAsync(request?.Ids, ct);
        if (items is null)
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            MarkAsRead(item);
            if (!item.IsArchived)
            {
                item.IsArchived = true;
                item.ArchivedAt = now;
            }
        }

        if (items.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return Ok(new UserNotificationBulkActionResultDto { UpdatedCount = items.Count });
    }

    [HttpPost("bulk/delete")]
    public async Task<ActionResult<UserNotificationBulkActionResultDto>> DeleteSelected([FromBody] BulkIdsRequest request, CancellationToken ct)
    {
        var items = await FindOwnedNotificationsAsync(request?.Ids, ct);
        if (items is null)
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.DeletedAt ??= now;
        }

        if (items.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return Ok(new UserNotificationBulkActionResultDto { UpdatedCount = items.Count });
    }

    [HttpPatch("{id:int}/read")]
    public async Task<ActionResult<UserNotificationDto>> MarkRead(int id, CancellationToken ct)
    {
        var item = await FindOwnedNotificationAsync(id, ct);
        if (item is null)
        {
            return NotFound();
        }

        MarkAsRead(item);
        await _context.SaveChangesAsync(ct);
        return Ok(Map(item));
    }

    [HttpPatch("{id:int}/unread")]
    public async Task<ActionResult<UserNotificationDto>> MarkUnread(int id, CancellationToken ct)
    {
        var item = await FindOwnedNotificationAsync(id, ct);
        if (item is null)
        {
            return NotFound();
        }

        item.IsRead = false;
        item.ReadAt = null;
        await _context.SaveChangesAsync(ct);
        return Ok(Map(item));
    }

    [HttpPatch("{id:int}/archive")]
    public async Task<ActionResult<UserNotificationDto>> Archive(int id, CancellationToken ct)
    {
        var item = await FindOwnedNotificationAsync(id, ct);
        if (item is null)
        {
            return NotFound();
        }

        MarkAsRead(item);
        if (!item.IsArchived)
        {
            item.IsArchived = true;
            item.ArchivedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(ct);
        return Ok(Map(item));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var item = await FindOwnedNotificationAsync(id, ct);
        if (item is null)
        {
            return NotFound();
        }

        item.DeletedAt ??= DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<UserNotification?> FindOwnedNotificationAsync(int id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var visibility = await ResolveNotificationTargetVisibilityAsync(ct);
        var query = _context.UserNotifications
            .Where(item =>
                item.Id == id
                && item.RecipientUserId == userId.Value
                && item.DeletedAt == null);
        var disabledTypes = await LoadDisabledNotificationTypesAsync(userId.Value, ct);
        query = ApplyDisabledTypeFilter(query, disabledTypes);
        query = WhereNotificationTargetVisible(query, _context, visibility);
        return await query
            .FirstOrDefaultAsync(ct);
    }

    private async Task<List<UserNotification>?> FindOwnedNotificationsAsync(IEnumerable<int>? ids, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var normalizedIds = ids?
            .Where(id => id > 0)
            .Distinct()
            .Take(100)
            .ToArray() ?? [];
        if (normalizedIds.Length == 0)
        {
            return [];
        }

        var visibility = await ResolveNotificationTargetVisibilityAsync(ct);
        var disabledTypes = await LoadDisabledNotificationTypesAsync(userId.Value, ct);
        var query = _context.UserNotifications
            .Where(item =>
                normalizedIds.Contains(item.Id)
                && item.RecipientUserId == userId.Value
                && item.DeletedAt == null);

        query = ApplyDisabledTypeFilter(query, disabledTypes);
        query = WhereNotificationTargetVisible(query, _context, visibility);
        return await query.ToListAsync(ct);
    }

    private async Task<NotificationTargetVisibility> ResolveNotificationTargetVisibilityAsync(CancellationToken ct)
    {
        var userId = User.GetUserId().GetValueOrDefault();
        var canViewNotes = userId > 0
            && await _context.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Notes, static permission => permission.CanView, ct);

        return new NotificationTargetVisibility(
            userId,
            canViewNotes,
            await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct),
            await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct),
            await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct),
            await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct),
            await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Acts, ct),
            await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Contracts, ct));
    }

    private async Task<IReadOnlyList<string>> LoadDisabledNotificationTypesAsync(int userId, CancellationToken ct)
    {
        var value = await _context.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId
                && item.Key == UserNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);

        return UserNotificationPreferenceSettings.ParseDisabledTypes(value);
    }

    private static IQueryable<UserNotification> ApplyStatusFilter(
        IQueryable<UserNotification> query,
        string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "all" => query,
            "unread" => query.Where(item => !item.IsArchived && !item.IsRead),
            "read" => query.Where(item => !item.IsArchived && item.IsRead),
            "archived" => query.Where(item => item.IsArchived),
            _ => query.Where(item => !item.IsArchived)
        };
    }

    private static IQueryable<UserNotification> ApplyTypeAndSearchFilters(
        IQueryable<UserNotification> query,
        string? type,
        string? search)
    {
        if (!string.IsNullOrWhiteSpace(type) && UserNotificationTypes.IsKnown(type))
        {
            var normalizedType = UserNotificationTypes.Find(type)!.Key;
            query = query.Where(item => item.Type == normalizedType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(item =>
                item.Title.ToLower().Contains(term)
                || item.BodyPreview.ToLower().Contains(term)
                || item.EventKey.ToLower().Contains(term));
        }

        return query;
    }

    private static IQueryable<UserNotification> ApplyDateRangeFilter(
        IQueryable<UserNotification> query,
        string? dateFrom,
        string? dateTo)
    {
        if (DateTime.TryParse(dateFrom, out var parsedFrom))
        {
            var from = parsedFrom.Date;
            query = query.Where(item => item.CreatedAt >= from);
        }

        if (DateTime.TryParse(dateTo, out var parsedTo))
        {
            var to = parsedTo.Date.AddDays(1);
            query = query.Where(item => item.CreatedAt < to);
        }

        return query;
    }

    private static IQueryable<UserNotification> ApplyDomainFilter(
        IQueryable<UserNotification> query,
        string? domain)
    {
        return NormalizeNotificationDomain(domain) switch
        {
            "task" => query.Where(item =>
                item.TaskJournalItemId.HasValue
                || item.ObjectType == "task"
                || item.Type == UserNotificationTypes.TaskAssigned
                || item.Type == UserNotificationTypes.TaskReminder
                || item.Type == UserNotificationTypes.TaskChanged
                || item.Type == UserNotificationTypes.TaskComment
                || item.Type == UserNotificationTypes.TaskCompleted
                || item.Type == UserNotificationTypes.TaskCancelled
                || item.Type == UserNotificationTypes.TaskScheduleConflict
                || item.Type == UserNotificationTypes.TaskApprovalRequested
                || item.Type == UserNotificationTypes.TaskApproved
                || item.Type == UserNotificationTypes.TaskRevisionRequested
                || item.Type == UserNotificationTypes.TaskApprovalOverdue),
            "calendar" => query.Where(item =>
                item.ObjectType == "calendar"
                || item.Type.Contains("Calendar")
                || item.Type == UserNotificationTypes.CalendarAccessChanged),
            "note" => query.Where(item =>
                item.ObjectType == "note"
                || item.Type.StartsWith("note")
                || item.Type == UserNotificationTypes.NoteMention
                || item.Type == UserNotificationTypes.NoteAssigned
                || item.Type == UserNotificationTypes.NoteReminder
                || item.Type == UserNotificationTypes.NoteImportantChanged),
            "finance" => query.Where(item =>
                item.ObjectType == "payment"
                || item.ObjectType == "invoice"
                || item.ObjectType == "account"
                || item.ObjectType == "act"
                || item.ObjectType == "contract"
                || item.Type == UserNotificationTypes.PaymentAssigned
                || item.Type == UserNotificationTypes.PaymentChanged
                || item.Type == UserNotificationTypes.PaymentAdvanceReviewRequired
                || item.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation),
            "case" => query.Where(item =>
                item.ObjectType == "case"
                || item.ObjectType == "client-case"
                || item.ObjectType == "client-case-document"
                || item.Type == UserNotificationTypes.CaseInactivityControl
                || item.Type == UserNotificationTypes.CaseAccessRequested
                || item.Type == UserNotificationTypes.CaseAccessResolved
                || item.Type == UserNotificationTypes.CaseCourtEvent
                || item.Type == UserNotificationTypes.CourtHearing
                || item.Type == UserNotificationTypes.CourtStatusChanged
                || item.Type == UserNotificationTypes.CourtDecisionPublished
                || item.Type == UserNotificationTypes.CourtSyncFailed
                || item.Type == UserNotificationTypes.CaseProceduralDeadline
                || item.Type == UserNotificationTypes.CaseDocument
                || item.Type == UserNotificationTypes.CaseInactivityReminder),
            "system" => query.Where(item =>
                item.Type == UserNotificationTypes.SystemAdminWarning
                || (item.ObjectType == string.Empty
                    && !item.ObjectId.HasValue
                    && !item.TaskJournalItemId.HasValue)),
            _ => query
        };
    }

    private static IQueryable<UserNotification> ApplyRequiresActionFilter(
        IQueryable<UserNotification> query,
        bool? requiresAction)
    {
        if (requiresAction != true)
        {
            return query;
        }

        return query.Where(item =>
            item.Type == UserNotificationTypes.TaskAssigned
            || item.Type == UserNotificationTypes.TaskReminder
            || item.Type == UserNotificationTypes.TaskCancelled
            || item.Type == UserNotificationTypes.TaskScheduleConflict
            || item.Type == UserNotificationTypes.TaskApprovalRequested
            || item.Type == UserNotificationTypes.TaskRevisionRequested
            || item.Type == UserNotificationTypes.TaskApprovalOverdue
            || item.Type == UserNotificationTypes.CalendarAccessChanged
            || item.Type == UserNotificationTypes.CaseInactivityControl
            || item.Type == UserNotificationTypes.CaseAccessRequested
            || item.Type == UserNotificationTypes.CaseCourtEvent
            || item.Type == UserNotificationTypes.CourtHearing
            || item.Type == UserNotificationTypes.CourtStatusChanged
            || item.Type == UserNotificationTypes.CourtDecisionPublished
            || item.Type == UserNotificationTypes.CourtSyncFailed
            || item.Type == UserNotificationTypes.CaseProceduralDeadline
            || item.Type == UserNotificationTypes.CaseInactivityReminder
            || item.Type == UserNotificationTypes.NoteMention
            || item.Type == UserNotificationTypes.NoteAssigned
            || item.Type == UserNotificationTypes.NoteReminder
            || item.Type == UserNotificationTypes.NoteImportantChanged
            || item.Type == UserNotificationTypes.PaymentAssigned
            || item.Type == UserNotificationTypes.PaymentChanged
            || item.Type == UserNotificationTypes.PaymentAdvanceReviewRequired
            || item.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation
            || item.Type == UserNotificationTypes.SystemAdminWarning);
    }

    private static string? NormalizeNotificationDomain(string? domain)
    {
        var value = domain?.Trim().ToLowerInvariant();
        return value is "task" or "calendar" or "note" or "finance" or "case" or "system"
            ? value
            : null;
    }

    private static IQueryable<UserNotification> ApplyTaskReferenceFilters(
        IQueryable<UserNotification> query,
        PaymentContext context,
        int? employeeId,
        int? clientId,
        string? taskRole)
    {
        var hasEmployeeFilter = employeeId.HasValue && employeeId.Value > 0;
        var hasClientFilter = clientId.HasValue && clientId.Value > 0;
        var role = NormalizeTaskRole(taskRole);

        if (!hasEmployeeFilter && !hasClientFilter && role is null)
        {
            return query;
        }

        var taskQuery = context.TaskJournalItems.AsNoTracking().AsQueryable();

        if (hasClientFilter)
        {
            var selectedClientId = clientId!.Value;
            taskQuery = taskQuery.Where(task =>
                task.ClientId == selectedClientId
                || (task.ClientCaseId.HasValue
                    && task.ClientCase != null
                    && task.ClientCase.ClientId == selectedClientId));
        }

        if (role is not null)
        {
            taskQuery = ApplyTaskRoleFilter(taskQuery, role, hasEmployeeFilter ? employeeId!.Value : null);
        }
        else if (hasEmployeeFilter)
        {
            var selectedEmployeeId = employeeId!.Value;
            taskQuery = taskQuery.Where(task =>
                task.AssigneeUserId == selectedEmployeeId
                || task.AuthorUserId == selectedEmployeeId
                || task.Participants.Any(participant => participant.UserId == selectedEmployeeId));
        }

        var taskIds = taskQuery.Select(task => task.Id);
        return query.Where(item =>
            item.TaskJournalItemId.HasValue
            && taskIds.Contains(item.TaskJournalItemId.Value));
    }

    private static IQueryable<TaskJournalItem> ApplyTaskRoleFilter(
        IQueryable<TaskJournalItem> query,
        string role,
        int? employeeId)
    {
        return role switch
        {
            "assignee" => employeeId.HasValue
                ? query.Where(task =>
                    task.AssigneeUserId == employeeId.Value
                    || task.Participants.Any(participant =>
                        participant.UserId == employeeId.Value
                        && participant.Role == "assignee"))
                : query.Where(task =>
                    task.AssigneeUserId.HasValue
                    || task.Participants.Any(participant => participant.Role == "assignee")),
            "author" => employeeId.HasValue
                ? query.Where(task => task.AuthorUserId == employeeId.Value)
                : query.Where(task => task.AuthorUserId.HasValue),
            "observer" => employeeId.HasValue
                ? query.Where(task => task.Participants.Any(participant =>
                    participant.UserId == employeeId.Value
                    && participant.Role == "observer"))
                : query.Where(task => task.Participants.Any(participant => participant.Role == "observer")),
            "approver" => employeeId.HasValue
                ? query.Where(task => task.Participants.Any(participant =>
                    participant.UserId == employeeId.Value
                    && participant.Role == "approver"))
                : query.Where(task => task.Participants.Any(participant => participant.Role == "approver")),
            _ => query
        };
    }

    private static string? NormalizeTaskRole(string? taskRole)
    {
        var value = taskRole?.Trim().ToLowerInvariant();
        return value is "assignee" or "author" or "observer" or "approver"
            ? value
            : null;
    }

    private static IQueryable<UserNotification> ApplyDisabledTypeFilter(
        IQueryable<UserNotification> query,
        IReadOnlyCollection<string> disabledTypes)
    {
        if (disabledTypes.Count == 0)
        {
            return query;
        }

        return query.Where(item => !disabledTypes.Contains(item.Type));
    }

    private static IQueryable<UserNotification> WhereNotificationTargetVisible(
        IQueryable<UserNotification> query,
        PaymentContext context,
        NotificationTargetVisibility visibility)
    {
        var visibleNoteIds = context.StickyNotes
            .Where(note =>
                visibility.CanViewNotes
                && note.Board != null
                && !note.Board.IsArchived
                && (note.Board.Visibility == StickyNoteBoardVisibility.Shared
                    || note.Board.OwnerUserId == visibility.UserId
                    || note.Board.Members.Any(member => member.UserId == visibility.UserId)))
            .Select(note => note.Id);

        if (visibility.HasNoAccess)
        {
            return query.Where(item =>
                !item.TaskJournalItemId.HasValue
                && !item.ObjectId.HasValue
                && item.ObjectType == string.Empty);
        }

        if (visibility.SeesAllTargets)
        {
            return query.Where(item =>
                item.ObjectType != "note"
                || (item.ObjectId.HasValue && visibleNoteIds.Contains(item.ObjectId.Value)));
        }

        var visibleTaskIds = context.TaskJournalItems
            .ApplyTaskVisibility(visibility.Tasks)
            .Select(task => task.Id);
        var visibleCaseIds = context.ClientCases
            .ApplyClientCaseVisibility(visibility.Cases)
            .Select(clientCase => clientCase.Id);
        var visibleCaseDocumentIds = context.ClientCaseDocuments
            .Where(document => visibleCaseIds.Contains(document.ClientCaseId))
            .Select(document => document.Id);
        var visibleAccountIds = context.Payments
            .ApplyPaymentVisibility(visibility.Accounts)
            .Select(payment => payment.Id);
        var visiblePaymentIds = context.Payments
            .ApplyPaymentVisibility(visibility.Payments)
            .Select(payment => payment.Id);
        var visibleActIds = context.Acts
            .ApplyActVisibility(visibility.Acts)
            .Select(act => act.Id);
        var visibleContractIds = context.Contracts
            .ApplyContractVisibility(visibility.Contracts)
            .Select(contract => contract.Id);

        return query.Where(item =>
            (!item.TaskJournalItemId.HasValue
                && !item.ObjectId.HasValue
                && item.ObjectType == string.Empty)
            || (item.TaskJournalItemId.HasValue
                && visibleTaskIds.Contains(item.TaskJournalItemId.Value))
            || (!item.TaskJournalItemId.HasValue
                && item.ObjectType == "task"
                && item.ObjectId.HasValue
                && visibleTaskIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "case"
                && item.ObjectId.HasValue
                && visibleCaseIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "client-case"
                && item.ObjectId.HasValue
                && visibleCaseIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "client-case-document"
                && item.ObjectId.HasValue
                && visibleCaseDocumentIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "invoice"
                && item.ObjectId.HasValue
                && visibleAccountIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "account"
                && item.ObjectId.HasValue
                && visibleAccountIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "payment"
                && item.ObjectId.HasValue
                && (visiblePaymentIds.Contains(item.ObjectId.Value)
                    || visibleAccountIds.Contains(item.ObjectId.Value)))
            || (item.ObjectType == "act"
                && item.ObjectId.HasValue
                && visibleActIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "contract"
                && item.ObjectId.HasValue
                && visibleContractIds.Contains(item.ObjectId.Value))
            || (item.ObjectType == "note"
                && item.ObjectId.HasValue
                && visibleNoteIds.Contains(item.ObjectId.Value)));
    }

    private static void MarkAsRead(UserNotification item)
    {
        if (item.IsRead)
        {
            return;
        }

        item.IsRead = true;
        item.ReadAt = DateTime.UtcNow;
    }

    private static UserNotificationDto Map(UserNotification item)
    {
        var type = UserNotificationTypes.Find(item.Type);
        return new UserNotificationDto
        {
            Id = item.Id,
            Type = item.Type,
            TypeLabel = type?.Label ?? item.Type,
            EventKey = item.EventKey,
            Title = item.Title,
            BodyPreview = item.BodyPreview,
            Tone = item.Tone,
            ObjectType = item.ObjectType,
            ObjectId = item.ObjectId,
            TaskId = item.TaskJournalItemId,
            IsRead = item.IsRead,
            ReadAt = item.ReadAt,
            IsArchived = item.IsArchived,
            ArchivedAt = item.ArchivedAt,
            CreatedAt = item.CreatedAt
        };
    }

    private static UserNotificationTypeDto MapType(UserNotificationTypeDefinition item)
        => new()
        {
            Key = item.Key,
            Label = item.Label,
            Description = item.Description,
            Tone = item.Tone,
            Priority = item.Priority,
            Channels = item.Channels,
            Deduplicate = item.Deduplicate,
            Digestible = item.Digestible
        };

    private sealed record NotificationTargetVisibility(
        int UserId,
        bool CanViewNotes,
        DataVisibilityAccess? Tasks,
        DataVisibilityAccess? Cases,
        DataVisibilityAccess? Accounts,
        DataVisibilityAccess? Payments,
        DataVisibilityAccess? Acts,
        DataVisibilityAccess? Contracts)
    {
        public bool HasNoAccess
            => !CanViewNotes
                && Tasks is null
                && Cases is null
                && Accounts is null
                && Payments is null
                && Acts is null
                && Contracts is null;

        public bool SeesAllTargets
            => Tasks?.SeesAll == true
                && Cases?.SeesAll == true
                && Accounts?.SeesAll == true
                && Payments?.SeesAll == true
                && Acts?.SeesAll == true
                && Contracts?.SeesAll == true;
    }
}
