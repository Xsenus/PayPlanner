using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.UserActivity;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/user-activity")]
[Authorize]
public class UserActivityController : ControllerBase, IAsyncActionFilter
{
    private const int OnlineThresholdMinutes = 5;
    private readonly PaymentContext _context;
    private readonly IUserActivityService _activityService;
    private readonly ISqliteMaintenanceService _sqliteMaintenanceService;
    private readonly ILogger<UserActivityController> _logger;

    public UserActivityController(
        PaymentContext context,
        IUserActivityService activityService,
        ISqliteMaintenanceService sqliteMaintenanceService,
        ILogger<UserActivityController> logger)
    {
        _context = context;
        _activityService = activityService;
        _sqliteMaintenanceService = sqliteMaintenanceService;
        _logger = logger;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var requiresAdmin = context.ActionDescriptor.EndpointMetadata
            .OfType<AuthorizeAttribute>()
            .Any(attribute => string.Equals(attribute.Policy, "Admin", StringComparison.Ordinal));
        if (!requiresAdmin)
        {
            await next();
            return;
        }

        var userId = context.HttpContext.User.GetUserId();
        if (!userId.HasValue || !await _context.IsUserAdminAsync(userId.Value, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> GetAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int? userId = null,
        [FromQuery] string? category = null,
        [FromQuery] string? action = null,
        [FromQuery] string? section = null,
        [FromQuery] string? httpMethod = null,
        [FromQuery] UserActivityStatus? status = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = _context.UserActivityLogs.AsNoTracking();

        if (from.HasValue)
        {
            query = query.Where(x => x.CreatedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(x => x.CreatedAt <= to.Value);
        }

        if (userId.HasValue)
        {
            query = query.Where(x => x.UserId == userId.Value);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(x => x.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => x.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(section))
        {
            query = query.Where(x => x.Section == section);
        }

        if (!string.IsNullOrWhiteSpace(httpMethod))
        {
            query = query.Where(x => x.HttpMethod == httpMethod);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x =>
                x.Description!.ToLower().Contains(term) ||
                x.ObjectId!.ToLower().Contains(term) ||
                x.ObjectType!.ToLower().Contains(term) ||
                x.Category!.ToLower().Contains(term) ||
                x.Action!.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtos = items.Select(MapToDto).ToList();

        return Ok(new
        {
            items = dtos,
            total,
            page,
            pageSize
        });
    }

    [HttpGet("online")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserOnlineSummaryDto>> GetOnlineAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var onlineAfter = now.AddMinutes(-OnlineThresholdMinutes);

        var users = await _context.Users
            .AsNoTracking()
            .Include(x => x.Role)
            .OrderBy(x => x.FullName)
            .ThenBy(x => x.Email)
            .Select(user => new UserOnlineStatusDto
            {
                UserId = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                RoleName = user.Role != null ? user.Role.Name : null,
                Department = user.DepartmentDictionary != null ? user.DepartmentDictionary.Name : user.Department,
                IsActive = user.IsActive,
                IsApproved = user.IsApproved,
                LastActivityAt = user.LastActivityAt ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id)
                    .Max(log => (DateTime?)log.CreatedAt),
                LastLoginAt = user.LastLoginAt ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id
                        && (log.Category == "auth" || log.Category == "Auth")
                        && (log.Action == "login" || log.Action == "Login"))
                    .Max(log => (DateTime?)log.CreatedAt),
                LastLogoutAt = user.LastLogoutAt ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id
                        && (log.Category == "auth" || log.Category == "Auth")
                        && (log.Action == "logout" || log.Action == "Logout"))
                    .Max(log => (DateTime?)log.CreatedAt),
                LastAction = user.LastActivityAction ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id)
                    .OrderByDescending(log => log.CreatedAt)
                    .ThenByDescending(log => log.Id)
                    .Select(log => log.Action)
                    .FirstOrDefault(),
                LastSection = user.LastActivitySection ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id)
                    .OrderByDescending(log => log.CreatedAt)
                    .ThenByDescending(log => log.Id)
                    .Select(log => log.Section)
                    .FirstOrDefault(),
                LastIpAddress = user.LastActivityIpAddress ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id)
                    .OrderByDescending(log => log.CreatedAt)
                    .ThenByDescending(log => log.Id)
                    .Select(log => log.IpAddress)
                    .FirstOrDefault(),
                LastUserAgent = user.LastActivityUserAgent ?? _context.UserActivityLogs
                    .Where(log => log.UserId == user.Id)
                    .OrderByDescending(log => log.CreatedAt)
                    .ThenByDescending(log => log.Id)
                    .Select(log => log.UserAgent)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            var latestAction = user.LastAction?.Trim();
            var latestActionIsLogout = string.Equals(latestAction, "logout", StringComparison.OrdinalIgnoreCase);
            user.IsOnline = user.IsActive
                && user.IsApproved
                && user.LastActivityAt.HasValue
                && user.LastActivityAt.Value >= onlineAfter
                && !latestActionIsLogout;
        }

        var orderedUsers = users
            .OrderByDescending(user => user.IsOnline)
            .ThenByDescending(user => user.LastActivityAt ?? DateTime.MinValue)
            .ThenBy(user => user.FullName)
            .ToList();

        return Ok(new UserOnlineSummaryDto
        {
            GeneratedAt = now,
            OnlineThresholdMinutes = OnlineThresholdMinutes,
            TotalUsers = orderedUsers.Count,
            OnlineUsers = orderedUsers.Count(user => user.IsOnline),
            Users = orderedUsers
        });
    }

    [HttpGet("filters")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> GetFiltersAsync(CancellationToken cancellationToken)
    {
        var query = _context.UserActivityLogs.AsNoTracking();

        var categories = await query
            .Where(x => x.Category != null && x.Category != "")
            .Select(x => x.Category!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var actions = await query
            .Where(x => x.Action != null && x.Action != "")
            .Select(x => x.Action!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var sections = await query
            .Where(x => x.Section != null && x.Section != "")
            .Select(x => x.Section!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var methods = await query
            .Where(x => x.HttpMethod != null && x.HttpMethod != "")
            .Select(x => x.HttpMethod!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var actors = await query
            .Where(x => x.UserId != null)
            .GroupBy(x => x.UserId!.Value)
            .Select(g => new UserActivityActorDto
            {
                Id = g.Key,
                FullName = g.Select(x => x.UserFullName).Where(x => x != null && x != "").OrderBy(x => x).FirstOrDefault(),
                Email = g.Select(x => x.UserEmail).Where(x => x != null && x != "").OrderBy(x => x).FirstOrDefault()
            })
            .OrderBy(x => x.FullName ?? x.Email ?? string.Empty)
            .ToListAsync(cancellationToken);

        var dto = new UserActivityFilterOptionsDto
        {
            Statuses = Enum.GetValues<UserActivityStatus>(),
            Categories = categories,
            Actions = actions,
            Sections = sections,
            HttpMethods = methods,
            Actors = actors
        };

        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] CreateUserActivityLogRequest request, CancellationToken cancellationToken)
    {
        await UpdateCurrentUserPresenceAsync(request.Action, request.Section ?? request.Category, cancellationToken);

        if (!(await _activityService.GetSettingsAsync(cancellationToken)).IsEnabled)
        {
            return NoContent();
        }

        var entry = new ActivityLogEntry
        {
            Category = request.Category,
            Action = request.Action,
            Section = request.Section,
            ObjectType = request.ObjectType,
            ObjectId = request.ObjectId,
            Description = request.Description,
            Status = request.Status ?? UserActivityStatus.Info,
            Metadata = request.Metadata ?? new Dictionary<string, object?>()
        };

        var log = await _activityService.TryWriteAsync(entry, cancellationToken);
        if (log is null)
        {
            _logger.LogWarning("Не удалось сохранить пользовательскую активность: {Category}/{Action}", request.Category, request.Action);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Не удалось сохранить запись активности." });
        }

        var dto = MapToDto(log);
        return Created($"/api/user-activity/{dto.Id}", dto);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> HeartbeatAsync(CancellationToken cancellationToken)
    {
        await UpdateCurrentUserPresenceAsync("heartbeat", "session", cancellationToken);
        return NoContent();
    }

    private async Task UpdateCurrentUserPresenceAsync(string? action, string? section, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }

        var normalizedAction = Truncate(string.IsNullOrWhiteSpace(action) ? "activity" : action.Trim(), 160);
        var normalizedSection = Truncate(string.IsNullOrWhiteSpace(section) ? null : section.Trim(), 160);
        var now = DateTime.UtcNow;
        var ip = Truncate(ResolveIpAddress(), 64);
        var userAgent = Truncate(Request.Headers.UserAgent.FirstOrDefault(), 512);
        var isLogin = string.Equals(normalizedAction, "login", StringComparison.OrdinalIgnoreCase);
        var isLogout = string.Equals(normalizedAction, "logout", StringComparison.OrdinalIgnoreCase);

        await _context.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.LastActivityAt, now)
                .SetProperty(user => user.LastActivityAction, normalizedAction)
                .SetProperty(user => user.LastActivitySection, normalizedSection)
                .SetProperty(user => user.LastActivityIpAddress, ip)
                .SetProperty(user => user.LastActivityUserAgent, userAgent)
                .SetProperty(user => user.LastLoginAt, user => isLogin ? now : user.LastLoginAt)
                .SetProperty(user => user.LastLogoutAt, user => isLogout ? now : user.LastLogoutAt),
                cancellationToken);
    }

    private string? ResolveIpAddress()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            return forwardedFor.Split(',').FirstOrDefault()?.Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    [HttpGet("admin/maintenance")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserActivityMaintenanceDto>> GetMaintenanceAsync(CancellationToken cancellationToken)
    {
        return Ok(await BuildMaintenanceAsync(cancellationToken));
    }

    [HttpPut("admin/settings")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserActivityMaintenanceDto>> UpdateSettingsAsync(
        [FromBody] UpdateUserActivitySettingsRequest request,
        CancellationToken cancellationToken)
    {
        await _activityService.UpdateSettingsAsync(request.IsEnabled, request.RetentionDays, cancellationToken);
        return Ok(await BuildMaintenanceAsync(cancellationToken));
    }

    [HttpPost("admin/cleanup")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserActivityCleanupResultDto>> CleanupAsync(CancellationToken cancellationToken)
    {
        return Ok(await CleanupLogsInternalAsync(cancellationToken));
    }

    [HttpPost("admin/compact")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserActivityCompactionResultDto>> CompactAsync(CancellationToken cancellationToken)
    {
        var (before, after) = await _sqliteMaintenanceService.CompactAsync(cancellationToken);
        return Ok(new UserActivityCompactionResultDto
        {
            Before = MapStorage(before),
            After = MapStorage(after),
            Maintenance = await BuildMaintenanceAsync(cancellationToken),
        });
    }

    [HttpDelete("cleanup")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> DeleteAllAsync([FromBody] DeleteUserActivityRequest? request, CancellationToken cancellationToken)
    {
        return Ok(await CleanupLogsInternalAsync(cancellationToken));
    }

    private static UserActivityLogDto MapToDto(UserActivityLog log)
    {
        return new UserActivityLogDto
        {
            Id = log.Id,
            UserId = log.UserId,
            UserEmail = log.UserEmail,
            UserFullName = log.UserFullName,
            Category = log.Category,
            Action = log.Action,
            Section = log.Section,
            ObjectType = log.ObjectType,
            ObjectId = log.ObjectId,
            Description = log.Description,
            Status = log.Status,
            IpAddress = log.IpAddress,
            UserAgent = log.UserAgent,
            HttpMethod = log.HttpMethod,
            Path = log.Path,
            QueryString = log.QueryString,
            HttpStatusCode = log.HttpStatusCode,
            DurationMs = log.DurationMs,
            Metadata = ParseMetadata(log.MetadataJson),
            CreatedAt = log.CreatedAt
        };
    }

    private static object? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object?>(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private async Task<UserActivityCleanupResultDto> CleanupLogsInternalAsync(CancellationToken cancellationToken)
    {
        var deleted = await _context.UserActivityLogs.ExecuteDeleteAsync(cancellationToken);
        await _sqliteMaintenanceService.CheckpointAsync(cancellationToken);

        _logger.LogInformation("Журнал активности пользователей очищен. Удалено: {Deleted}", deleted);

        return new UserActivityCleanupResultDto
        {
            DeletedLogs = deleted,
            Maintenance = await BuildMaintenanceAsync(cancellationToken),
        };
    }

    private async Task<UserActivityMaintenanceDto> BuildMaintenanceAsync(CancellationToken cancellationToken)
    {
        var settings = await _activityService.GetSettingsAsync(cancellationToken);
        var logEntries = await _context.UserActivityLogs.AsNoTracking().CountAsync(cancellationToken);

        DateTime? oldestLogAt = null;
        DateTime? newestLogAt = null;

        if (logEntries > 0)
        {
            oldestLogAt = await _context.UserActivityLogs.AsNoTracking()
                .MinAsync(x => (DateTime?)x.CreatedAt, cancellationToken);
            newestLogAt = await _context.UserActivityLogs.AsNoTracking()
                .MaxAsync(x => (DateTime?)x.CreatedAt, cancellationToken);
        }

        return new UserActivityMaintenanceDto
        {
            IsEnabled = settings.IsEnabled,
            RetentionDays = settings.RetentionDays,
            UpdatedAt = settings.UpdatedAt,
            LogEntries = logEntries,
            OldestLogAt = oldestLogAt,
            NewestLogAt = newestLogAt,
            Storage = MapStorage(await _sqliteMaintenanceService.GetStorageStatsAsync(cancellationToken)),
        };
    }

    private static SqliteStorageStatsDto MapStorage(SqliteStorageStats storage) => new()
    {
        DatabaseFileSizeBytes = storage.DatabaseFileSizeBytes,
        WalFileSizeBytes = storage.WalFileSizeBytes,
        ShmFileSizeBytes = storage.ShmFileSizeBytes,
        TotalSizeBytes = storage.TotalSizeBytes,
        EstimatedReclaimableBytes = storage.EstimatedReclaimableBytes,
    };
}
