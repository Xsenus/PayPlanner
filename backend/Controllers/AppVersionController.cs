using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/app-version")]
public sealed class AppVersionController : ControllerBase
{
    private const string LegacySeenReleaseSettingKey = "app.releaseNotes.seenReleaseId";
    private static readonly HashSet<string> AllowedItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "new",
        "improved",
        "fixed",
        "important",
    };

    private readonly PaymentContext _db;
    private readonly TimeProvider _timeProvider;

    public AppVersionController(PaymentContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private async Task<ForbidResult?> RequireAdminAccessAsync(CancellationToken ct)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (!await _db.IsUserAdminOrLegacyFallbackAsync(User, ct))
        {
            return Forbid();
        }

        return null;
    }

    [HttpGet("latest")]
    public async Task<ActionResult<AppVersionLatestResponse>> GetLatest(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var release = await GetPublishedActiveReleaseQuery()
            .FirstOrDefaultAsync(ct);

        if (release is null)
        {
            return Ok(new AppVersionLatestResponse(GetAssemblyVersion(), null, true));
        }

        var seen = await _db.AppReleaseSeen
            .AsNoTracking()
            .AnyAsync(item => item.UserId == userId.Value && item.AppReleaseId == release.Id, ct);

        if (!seen)
        {
            seen = await _db.UserSettings
                .AsNoTracking()
                .AnyAsync(item =>
                    item.UserId == userId.Value
                    && item.Key == LegacySeenReleaseSettingKey
                    && item.Value == release.ReleaseId,
                    ct);
        }

        var dto = MapRelease(release);
        return Ok(new AppVersionLatestResponse(dto.Version, dto, seen));
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyCollection<AppReleaseDto>>> GetHistory(CancellationToken ct)
    {
        var releases = await GetPublishedActiveReleaseQuery().ToArrayAsync(ct);
        return Ok(releases.Select(MapRelease).ToArray());
    }

    [HttpPost("mark-seen")]
    public async Task<IActionResult> MarkSeen([FromBody] AppVersionMarkSeenRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var releaseId = request.ReleaseId?.Trim();
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return BadRequest("releaseId is required.");
        }

        var release = await _db.AppReleases
            .FirstOrDefaultAsync(item => item.IsActive && item.ReleaseId == releaseId, ct);
        if (release is null)
        {
            return NotFound();
        }

        var exists = await _db.AppReleaseSeen
            .AnyAsync(item => item.UserId == userId.Value && item.AppReleaseId == release.Id, ct);
        if (!exists)
        {
            _db.AppReleaseSeen.Add(new AppReleaseSeen
            {
                UserId = userId.Value,
                AppReleaseId = release.Id,
                SeenAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    [HttpGet("admin/releases")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IReadOnlyCollection<AppReleaseAdminDto>>> GetAdminReleases(CancellationToken ct)
    {
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var releases = await _db.AppReleases
            .AsNoTracking()
            .Include(release => release.Items)
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id)
            .ToArrayAsync(ct);

        return Ok(releases.Select(MapAdminRelease).ToArray());
    }

    [HttpGet("admin/releases/paged")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<PagedResponse<AppReleaseAdminDto>>> GetAdminReleasesPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);

        var query = _db.AppReleases.AsNoTracking();
        var normalizedSearch = search?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch) && _db.Database.IsNpgsql())
        {
            var searchPattern = $"%{EscapeLikePattern(normalizedSearch)}%";
            query = query.Where(release =>
                EF.Functions.ILike(release.ReleaseId, searchPattern, "\\")
                || EF.Functions.ILike(release.Version, searchPattern, "\\")
                || EF.Functions.ILike(release.Title, searchPattern, "\\"));
        }

        // SQLite LOWER/NOCASE обрабатывает только ASCII. В тестовой и локальной SQLite-БД
        // сначала читаем компактную проекцию без пунктов релизов и применяем Unicode-поиск
        // в памяти; PostgreSQL выше сохраняет полностью серверную пагинацию через ILIKE.
        if (!string.IsNullOrWhiteSpace(normalizedSearch) && !_db.Database.IsNpgsql())
        {
            var candidates = await query
                .Select(release => new
                {
                    release.Id,
                    release.ReleaseId,
                    release.Version,
                    release.Title,
                    release.ReleasedAt,
                })
                .ToArrayAsync(ct);
            var matchingIds = candidates
                .Where(release =>
                    release.ReleaseId.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || release.Version.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || release.Title.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(release => release.ReleasedAt)
                .ThenByDescending(release => release.Id)
                .Select(release => release.Id)
                .ToArray();
            var pagedIds = matchingIds
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            var pageReleases = await _db.AppReleases
                .AsNoTracking()
                .Include(release => release.Items)
                .Where(release => pagedIds.Contains(release.Id))
                .OrderByDescending(release => release.ReleasedAt)
                .ThenByDescending(release => release.Id)
                .ToArrayAsync(ct);

            return Ok(new PagedResponse<AppReleaseAdminDto>
            {
                Items = pageReleases.Select(MapAdminRelease).ToArray(),
                Total = matchingIds.Length,
                Page = page,
                PageSize = pageSize,
            });
        }

        var total = await query.CountAsync(ct);
        var releases = await query
            .Include(release => release.Items)
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(ct);

        return Ok(new PagedResponse<AppReleaseAdminDto>
        {
            Items = releases.Select(MapAdminRelease).ToArray(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    [HttpPost("admin/releases")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<AppReleaseAdminDto>> CreateRelease([FromBody] SaveAppReleaseRequest request, CancellationToken ct)
    {
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var validation = ValidateRequest(request);
        if (validation is not null) return BadRequest(validation);

        var releaseId = request.ReleaseId.Trim();
        var exists = await _db.AppReleases.AnyAsync(item => item.ReleaseId == releaseId, ct);
        if (exists) return Conflict($"Release '{releaseId}' already exists.");

        var version = request.Version.Trim();
        var versionExists = await _db.AppReleases.AnyAsync(item => item.Version == version, ct);
        if (versionExists) return Conflict($"Version '{version}' already exists.");

        var now = DateTime.UtcNow;
        var release = new AppRelease
        {
            ReleaseId = releaseId,
            Version = version,
            ReleasedAt = request.ReleasedAt,
            Title = request.Title.Trim(),
            Summary = request.Summary?.Trim() ?? string.Empty,
            IsActive = request.IsActive,
            Source = "manual",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = User.GetUserId(),
            CreatedByUserName = User.GetDisplayName(),
            UpdatedByUserId = User.GetUserId(),
            UpdatedByUserName = User.GetDisplayName(),
            Items = BuildItems(request.Items),
        };

        _db.AppReleases.Add(release);
        await _db.SaveChangesAsync(ct);

        return Ok(MapAdminRelease(release));
    }

    [HttpPut("admin/releases/{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<AppReleaseAdminDto>> UpdateRelease(int id, [FromBody] SaveAppReleaseRequest request, CancellationToken ct)
    {
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var validation = ValidateRequest(request);
        if (validation is not null) return BadRequest(validation);

        var release = await _db.AppReleases
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (release is null) return NotFound();

        var releaseId = request.ReleaseId.Trim();
        var duplicate = await _db.AppReleases.AnyAsync(item => item.Id != id && item.ReleaseId == releaseId, ct);
        if (duplicate) return Conflict($"Release '{releaseId}' already exists.");

        var version = request.Version.Trim();
        var duplicateVersion = await _db.AppReleases.AnyAsync(item => item.Id != id && item.Version == version, ct);
        if (duplicateVersion) return Conflict($"Version '{version}' already exists.");

        release.ReleaseId = releaseId;
        release.Version = version;
        release.ReleasedAt = request.ReleasedAt;
        release.Title = request.Title.Trim();
        release.Summary = request.Summary?.Trim() ?? string.Empty;
        release.IsActive = request.IsActive;
        release.UpdatedAt = DateTime.UtcNow;
        release.UpdatedByUserId = User.GetUserId();
        release.UpdatedByUserName = User.GetDisplayName();

        _db.AppReleaseItems.RemoveRange(release.Items);
        release.Items = BuildItems(request.Items);

        await _db.SaveChangesAsync(ct);
        return Ok(MapAdminRelease(release));
    }

    [HttpDelete("admin/releases/{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> DeleteRelease(int id, CancellationToken ct)
    {
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var release = await _db.AppReleases
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (release is null) return NotFound();

        _db.AppReleases.Remove(release);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<AppRelease> GetPublishedActiveReleaseQuery()
    {
        var nowLocal = _timeProvider.GetLocalNow().DateTime;

        return _db.AppReleases
            .AsNoTracking()
            .Include(release => release.Items)
            .Where(release => release.IsActive && release.ReleasedAt <= nowLocal)
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id);
    }

    private static string? ValidateRequest(SaveAppReleaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReleaseId)) return "releaseId is required.";
        if (string.IsNullOrWhiteSpace(request.Version)) return "version is required.";
        if (string.IsNullOrWhiteSpace(request.Title)) return "title is required.";
        if (request.Items.Count == 0) return "items are required.";
        if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.Text))) return "item text is required.";
        return null;
    }

    private static List<AppReleaseItem> BuildItems(IReadOnlyCollection<SaveAppReleaseItemRequest> items)
        => items
            .Select((item, index) => new AppReleaseItem
            {
                Type = NormalizeItemType(item.Type),
                Text = item.Text.Trim(),
                SortOrder = index,
            })
            .ToList();

    private static AppReleaseDto MapRelease(AppRelease release)
        => new(
            release.Id,
            release.ReleaseId,
            release.Version,
            release.ReleasedAt,
            release.Title,
            release.Summary,
            release.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item => new AppReleaseItemDto(NormalizeItemType(item.Type), item.Text))
                .ToArray());

    private static AppReleaseAdminDto MapAdminRelease(AppRelease release)
    {
        var dto = MapRelease(release);
        return new AppReleaseAdminDto(
            dto.Id,
            dto.ReleaseId,
            dto.Version,
            dto.ReleasedAt,
            dto.Title,
            dto.Summary,
            dto.Items,
            release.IsActive,
            release.Source,
            release.CreatedAt,
            release.UpdatedAt);
    }

    private static string NormalizeItemType(string? type)
        => !string.IsNullOrWhiteSpace(type) && AllowedItemTypes.Contains(type)
            ? type.Trim().ToLowerInvariant()
            : "improved";

    private static string GetAssemblyVersion()
        => typeof(AppVersionController).Assembly.GetName().Version?.ToString() ?? "dev";
}

public sealed record AppVersionLatestResponse(
    string CurrentVersion,
    AppReleaseDto? LatestRelease,
    bool SeenByCurrentUser);

public sealed record AppReleaseDto(
    int Id,
    string ReleaseId,
    string Version,
    DateTime ReleasedAt,
    string Title,
    string Summary,
    IReadOnlyCollection<AppReleaseItemDto> Items);

public sealed record AppReleaseAdminDto(
    int Id,
    string ReleaseId,
    string Version,
    DateTime ReleasedAt,
    string Title,
    string Summary,
    IReadOnlyCollection<AppReleaseItemDto> Items,
    bool IsActive,
    string Source,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AppReleaseItemDto(string Type, string Text);

public sealed record AppVersionMarkSeenRequest(string ReleaseId);

public sealed record SaveAppReleaseRequest(
    string ReleaseId,
    string Version,
    DateTime ReleasedAt,
    string Title,
    string? Summary,
    bool IsActive,
    IReadOnlyCollection<SaveAppReleaseItemRequest> Items);

public sealed record SaveAppReleaseItemRequest(string Type, string Text);
