using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

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

    public AppVersionController(PaymentContext db) => _db = db;

    [HttpGet("latest")]
    public async Task<ActionResult<AppVersionLatestResponse>> GetLatest(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var release = await GetActiveReleaseQuery()
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
        var releases = await GetActiveReleaseQuery().ToArrayAsync(ct);
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
        var releases = await _db.AppReleases
            .AsNoTracking()
            .Include(release => release.Items)
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id)
            .ToArrayAsync(ct);

        return Ok(releases.Select(MapAdminRelease).ToArray());
    }

    [HttpPost("admin/releases")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<AppReleaseAdminDto>> CreateRelease([FromBody] SaveAppReleaseRequest request, CancellationToken ct)
    {
        var validation = ValidateRequest(request);
        if (validation is not null) return BadRequest(validation);

        var releaseId = request.ReleaseId.Trim();
        var exists = await _db.AppReleases.AnyAsync(item => item.ReleaseId == releaseId, ct);
        if (exists) return Conflict($"Release '{releaseId}' already exists.");

        var now = DateTime.UtcNow;
        var release = new AppRelease
        {
            ReleaseId = releaseId,
            Version = request.Version.Trim(),
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
        var validation = ValidateRequest(request);
        if (validation is not null) return BadRequest(validation);

        var release = await _db.AppReleases
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (release is null) return NotFound();

        var releaseId = request.ReleaseId.Trim();
        var duplicate = await _db.AppReleases.AnyAsync(item => item.Id != id && item.ReleaseId == releaseId, ct);
        if (duplicate) return Conflict($"Release '{releaseId}' already exists.");

        release.ReleaseId = releaseId;
        release.Version = request.Version.Trim();
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
        var release = await _db.AppReleases
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (release is null) return NotFound();

        _db.AppReleases.Remove(release);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<AppRelease> GetActiveReleaseQuery()
        => _db.AppReleases
            .AsNoTracking()
            .Include(release => release.Items)
            .Where(release => release.IsActive)
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id);

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
