using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using System.Security.Claims;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/user-settings")]
[Authorize]
public class UserSettingsController : ControllerBase, IAsyncActionFilter
{
    private readonly PaymentContext _db;

    public UserSettingsController(PaymentContext db) => _db = db;

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

        if (!await _db.IsUserAdminOrLegacyFallbackAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet("{key}")]
    public async Task<ActionResult<UserSettingDto>> Get(string key)
    {
        var userId = GetUserId(User);
        if (userId is null) return Unauthorized();

        var setting = await _db.UserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId.Value && s.Key == key);

        if (setting is null) return NoContent();

        return Ok(Map(setting));
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<UserSettingDto>> Upsert(string key, [FromBody] UpsertUserSettingRequest request)
    {
        var userId = GetUserId(User);
        if (userId is null) return Unauthorized();

        return await UpsertForUser(userId.Value, key, request);
    }

    [HttpPut("{key}/initialize")]
    public async Task<ActionResult<UserSettingDto>> Initialize(string key, [FromBody] UpsertUserSettingRequest request)
    {
        var userId = GetUserId(User);
        if (userId is null) return Unauthorized();
        return await WriteSettingAsync(userId.Value, key, request, onlyIfMissing: true);
    }

    [HttpGet("users/{userId:int}/{key}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserSettingDto>> GetForUser(int userId, string key)
    {
        var setting = await _db.UserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Key == key);

        if (setting is null) return NotFound();

        return Ok(Map(setting));
    }

    [HttpPut("users/{userId:int}/{key}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<UserSettingDto>> UpsertForUser(int userId, string key, [FromBody] UpsertUserSettingRequest request)
        => await WriteSettingAsync(userId, key, request, onlyIfMissing: false);

    private async Task<ActionResult<UserSettingDto>> WriteSettingAsync(int userId, string key, UpsertUserSettingRequest request, bool onlyIfMissing)
    {
        var exists = await _db.Users.AsNoTracking().AnyAsync(user => user.Id == userId);
        if (!exists) return NotFound();

        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || request.Value is null) return BadRequest();
        if (!_db.Database.IsNpgsql() && !_db.Database.IsSqlite())
            throw new InvalidOperationException("User settings require a supported relational provider.");

        var now = DateTime.UtcNow;
        // Match the existing UTC-by-convention timestamp columns on both providers.
        var storedNow = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);
        object createdAt = _db.Database.IsNpgsql()
            ? new NpgsqlParameter("createdAt", NpgsqlDbType.Timestamp) { Value = storedNow }
            : storedNow;
        object updatedAt = _db.Database.IsNpgsql()
            ? new NpgsqlParameter("updatedAt", NpgsqlDbType.Timestamp) { Value = storedNow }
            : storedNow;
        var ct = HttpContext.RequestAborted;
        // Both providers implement the same unique (UserId, Key) conflict boundary.
        // A late default can never replace an explicit choice; an explicit write
        // also remains an atomic upsert when the default won the first insert.
        // UserSetting is excluded from PaymentContext's audited entity registry.
        if (onlyIfMissing)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UserSettings" ("UserId", "Key", "Value", "CreatedAt", "UpdatedAt")
                VALUES ({userId}, {key}, {request.Value}, {createdAt}, {updatedAt})
                ON CONFLICT ("UserId", "Key") DO NOTHING
                """, ct);
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UserSettings" ("UserId", "Key", "Value", "CreatedAt", "UpdatedAt")
                VALUES ({userId}, {key}, {request.Value}, {createdAt}, {updatedAt})
                ON CONFLICT ("UserId", "Key") DO UPDATE
                SET "Value" = excluded."Value", "UpdatedAt" = excluded."UpdatedAt"
                """, ct);
            // Keep the legacy response for an explicit write, including its UTC timestamp.
            return Ok(new UserSettingDto { Key = key, Value = request.Value, UpdatedAt = now });
        }

        var setting = await _db.UserSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.Key == key, ct);
        return setting is null ? NotFound() : Ok(Map(setting));
    }

    private static int? GetUserId(ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (int?)null;

    private static UserSettingDto Map(UserSetting setting) => new()
    {
        Key = setting.Key,
        Value = setting.Value,
        UpdatedAt = setting.UpdatedAt,
    };
}
