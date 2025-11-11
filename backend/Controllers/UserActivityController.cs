using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.UserActivity;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserActivityController : ControllerBase
{
    private readonly PaymentContext _context;
    private readonly IUserActivityService _activityService;
    private readonly ILogger<UserActivityController> _logger;

    public UserActivityController(
        PaymentContext context,
        IUserActivityService activityService,
        ILogger<UserActivityController> logger)
    {
        _context = context;
        _activityService = activityService;
        _logger = logger;
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
            var like = $"%{search.Trim()}%";
            query = query.Where(x =>
                EF.Functions.Like(x.Description!, like) ||
                EF.Functions.Like(x.ObjectId!, like) ||
                EF.Functions.Like(x.ObjectType!, like) ||
                EF.Functions.Like(x.Category!, like) ||
                EF.Functions.Like(x.Action!, like));
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
}
