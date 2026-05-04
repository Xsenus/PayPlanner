using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "inProgress", "waiting", "done", "cancelled"
    };
    private static readonly HashSet<string> AllowedPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "low", "normal", "high", "urgent"
    };
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "task", "budget", "contact", "document", "reminder", "control"
    };
    private static readonly HashSet<string> AllowedReminders = new(StringComparer.OrdinalIgnoreCase)
    {
        "atDue", "before15m", "before1h", "before1d", "dailyOverdue"
    };
    private static readonly HashSet<string> AllowedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "inApp", "email", "telegram"
    };

    private readonly PaymentContext _db;

    public TasksController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<TaskJournalItemDto>>> GetTasks(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] int? assigneeUserId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var query = BuildVisibleTasksQuery(access);

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(task => task.Status != "done" && task.Status != "cancelled");
            }
            else
            {
                var normalizedStatus = NormalizeEnumValue(status, AllowedStatuses, "new");
                query = query.Where(task => task.Status == normalizedStatus);
            }
        }

        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "all", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedType = NormalizeEnumValue(type, AllowedTypes, "task");
            query = query.Where(task => task.Type == normalizedType);
        }

        if (assigneeUserId.HasValue)
        {
            query = query.Where(task => task.AssigneeUserId == assigneeUserId.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(task => task.DueAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(task => task.DueAt <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(task =>
                task.ObjectName.ToLower().Contains(term) ||
                task.Title.ToLower().Contains(term) ||
                (task.Description ?? "").ToLower().Contains(term) ||
                task.AssigneeName.ToLower().Contains(term) ||
                task.AuthorName.ToLower().Contains(term) ||
                (task.Result ?? "").ToLower().Contains(term) ||
                task.TagsJson.ToLower().Contains(term));
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(task => task.DueAt)
            .ThenBy(task => task.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<TaskJournalItemDto>
        {
            Items = items.Select(Map).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("notifications")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalNotificationDto>>> GetNotifications(CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;
        var soon = now.AddHours(1);
        var tasks = await BuildVisibleTasksQuery(access)
            .Where(task => task.Status != "done" && task.Status != "cancelled" && task.DueAt <= soon)
            .OrderBy(task => task.DueAt)
            .Take(20)
            .ToListAsync(ct);

        return Ok(tasks.Select(task =>
        {
            var overdue = task.DueAt < now;
            return new TaskJournalNotificationDto
            {
                Id = $"{task.Id}-{(overdue ? "overdue" : "soon")}",
                TaskId = task.Id,
                Title = overdue ? "Просрочена задача" : "Скоро срок",
                Description = $"{task.ObjectName}: {task.Title}",
                Tone = overdue ? "rose" : "amber",
                DueAt = ToIso(task.DueAt),
            };
        }).ToList());
    }

    [HttpGet("assignees")]
    public async Task<ActionResult<IReadOnlyList<TaskJournalAssigneeDto>>> GetAssignees(CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var users = await _db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved)
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .Select(user => new TaskJournalAssigneeDto
            {
                Id = user.Id,
                Name = user.FullName == "" ? user.Email : user.FullName,
                Email = user.Email,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TaskJournalItemDto>> GetTask(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanView != true)
        {
            return Forbid();
        }

        var task = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        return task is null ? NotFound() : Ok(Map(task));
    }

    [HttpPost]
    public async Task<ActionResult<TaskJournalItemDto>> CreateTask([FromBody] UpsertTaskJournalItemRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanCreate != true)
        {
            return Forbid();
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var assignee = await ResolveAssigneeAsync(request, access.UserId, ct);
        if (assignee is null)
        {
            return BadRequest(new { message = "Указанный исполнитель не найден." });
        }

        var entity = new TaskJournalItem
        {
            ObjectName = request.ObjectName.Trim(),
            Title = request.Title.Trim(),
            Description = NormalizeOptional(request.Description),
            AssigneeUserId = assignee.Value.UserId,
            AssigneeName = assignee.Value.Name,
            AuthorUserId = access.UserId,
            AuthorName = access.DisplayName,
            DueAt = request.DueAt,
            Type = NormalizeEnumValue(request.Type, AllowedTypes, "task"),
            Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal"),
            Status = NormalizeEnumValue(request.Status, AllowedStatuses, "new"),
            Result = NormalizeOptional(request.Result),
            RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]),
            ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]),
            TagsJson = SerializeFreeList(request.Tags),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.TaskJournalItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/tasks/{entity.Id}", Map(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TaskJournalItemDto>> UpdateTask(int id, [FromBody] UpsertTaskJournalItemRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanEdit != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var assignee = await ResolveAssigneeAsync(request, entity.AssigneeUserId ?? access.UserId, ct);
        if (assignee is null)
        {
            return BadRequest(new { message = "Указанный исполнитель не найден." });
        }

        entity.ObjectName = request.ObjectName.Trim();
        entity.Title = request.Title.Trim();
        entity.Description = NormalizeOptional(request.Description);
        entity.AssigneeUserId = assignee.Value.UserId;
        entity.AssigneeName = assignee.Value.Name;
        entity.DueAt = request.DueAt;
        entity.Type = NormalizeEnumValue(request.Type, AllowedTypes, "task");
        entity.Priority = NormalizeEnumValue(request.Priority, AllowedPriorities, "normal");
        entity.Status = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        entity.Result = NormalizeOptional(request.Result);
        entity.RemindersJson = SerializeList(request.Reminders, AllowedReminders, ["atDue"]);
        entity.ChannelsJson = SerializeList(request.Channels, AllowedChannels, ["inApp"]);
        entity.TagsJson = SerializeFreeList(request.Tags);
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(Map(entity));
    }

    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<TaskJournalItemDto>> UpdateStatus(int id, [FromBody] UpdateTaskJournalStatusRequest request, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanEdit != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        entity.Status = NormalizeEnumValue(request.Status, AllowedStatuses, "new");
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Map(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTask(int id, CancellationToken ct)
    {
        var access = await GetTaskAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin && access.Permission?.CanDelete != true)
        {
            return Forbid();
        }

        var entity = await BuildVisibleTasksQuery(access).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        _db.TaskJournalItems.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<TaskJournalItem> BuildVisibleTasksQuery(TaskAccess access)
    {
        var query = _db.TaskJournalItems.AsQueryable();
        return access.IsAdmin
            ? query
            : query.Where(task => task.AssigneeUserId == access.UserId || task.AuthorUserId == access.UserId);
    }

    private async Task<TaskAccess?> GetTaskAccessAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var isAdmin = await _db.IsUserAdminAsync(userId.Value, ct);
        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Tasks, ct);
        return new TaskAccess(userId.Value, User.GetDisplayName(), isAdmin, permission);
    }

    private async Task<(int? UserId, string Name)?> ResolveAssigneeAsync(
        UpsertTaskJournalItemRequest request,
        int fallbackUserId,
        CancellationToken ct)
    {
        if (request.AssigneeUserId.HasValue)
        {
            var user = await _db.Users
                .AsNoTracking()
                .Where(item => item.Id == request.AssigneeUserId.Value && item.IsActive && item.IsApproved)
                .Select(item => new { item.Id, item.FullName, item.Email })
                .FirstOrDefaultAsync(ct);

            return user is null
                ? null
                : (user.Id, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName);
        }

        var name = NormalizeOptional(request.AssigneeName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return (null, name);
        }

        var fallback = await _db.Users
            .AsNoTracking()
            .Where(item => item.Id == fallbackUserId && item.IsActive)
            .Select(item => new { item.Id, item.FullName, item.Email })
            .FirstOrDefaultAsync(ct);

        return fallback is null
            ? null
            : (fallback.Id, string.IsNullOrWhiteSpace(fallback.FullName) ? fallback.Email : fallback.FullName);
    }

    private static string? ValidateRequest(UpsertTaskJournalItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectName))
        {
            return "Укажите объект задачи.";
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return "Укажите текст задачи.";
        }

        if (request.DueAt == default)
        {
            return "Укажите дату и время исполнения.";
        }

        return null;
    }

    private static TaskJournalItemDto Map(TaskJournalItem entity)
        => new()
        {
            Id = entity.Id,
            ObjectName = entity.ObjectName,
            Title = entity.Title,
            Description = entity.Description ?? string.Empty,
            AssigneeUserId = entity.AssigneeUserId,
            AssigneeName = entity.AssigneeName,
            AuthorUserId = entity.AuthorUserId,
            AuthorName = entity.AuthorName,
            DueAt = ToIso(entity.DueAt),
            Type = entity.Type,
            Priority = entity.Priority,
            Status = entity.Status,
            Result = entity.Result ?? string.Empty,
            Reminders = DeserializeList(entity.RemindersJson),
            Channels = DeserializeList(entity.ChannelsJson),
            Tags = DeserializeList(entity.TagsJson),
            CreatedAt = ToIso(entity.CreatedAt),
            UpdatedAt = entity.UpdatedAt.HasValue ? ToIso(entity.UpdatedAt.Value) : null,
        };

    private static string ToIso(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O");

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeEnumValue(string? value, HashSet<string> allowedValues, string fallback)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return fallback;
        }

        return allowedValues.FirstOrDefault(allowed => string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private static string SerializeList(IEnumerable<string>? values, HashSet<string> allowedValues, IReadOnlyCollection<string> fallback)
    {
        var normalized = (values ?? Array.Empty<string>())
            .Select(value => NormalizeEnumValue(value, allowedValues, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized.Count > 0 ? normalized : fallback, JsonOptions);
    }

    private static string SerializeFreeList(IEnumerable<string>? values)
    {
        var normalized = (values ?? Array.Empty<string>())
            .Select(NormalizeOptional)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyList<string> DeserializeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private sealed record TaskAccess(int UserId, string DisplayName, bool IsAdmin, RolePermission? Permission);
}

public class TaskJournalNotificationDto
{
    public string Id { get; set; } = string.Empty;
    public int TaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tone { get; set; } = "blue";
    public string DueAt { get; set; } = string.Empty;
}
