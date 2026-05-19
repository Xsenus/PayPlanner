using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
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

        var visibility = await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        var unreadQuery = _context.UserNotifications
            .AsNoTracking()
            .Where(item =>
                item.RecipientUserId == userId.Value
                && !item.IsRead
                && !item.IsArchived
                && item.DeletedAt == null);
        var count = await WhereNotificationTargetVisible(unreadQuery, _context, visibility)
            .CountAsync(ct);

        return Ok(count);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<UserNotificationDto>>> GetNotifications(
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] string? search,
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
        var visibility = await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);

        query = ApplyStatusFilter(query, status);
        query = ApplyTypeAndSearchFilters(query, type, search);
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
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var query = _context.UserNotifications
            .Where(item => item.RecipientUserId == userId.Value && item.DeletedAt == null);
        var visibility = await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);

        query = ApplyStatusFilter(query, status);
        query = ApplyTypeAndSearchFilters(query, type, search);
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

        var visibility = await _context.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        var query = _context.UserNotifications
            .Where(item =>
                item.Id == id
                && item.RecipientUserId == userId.Value
                && item.DeletedAt == null);
        query = WhereNotificationTargetVisible(query, _context, visibility);
        return await query
            .FirstOrDefaultAsync(ct);
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

    private static IQueryable<UserNotification> WhereNotificationTargetVisible(
        IQueryable<UserNotification> query,
        PaymentContext context,
        DataVisibilityAccess? visibility)
    {
        if (visibility is null)
        {
            return query.Where(item => !item.TaskJournalItemId.HasValue);
        }

        if (visibility.SeesAll)
        {
            return query;
        }

        var visibleTaskIds = context.TaskJournalItems
            .ApplyTaskVisibility(visibility)
            .Select(task => task.Id);

        return query.Where(item =>
            !item.TaskJournalItemId.HasValue
            || visibleTaskIds.Contains(item.TaskJournalItemId.Value));
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
}
