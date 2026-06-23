using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
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
    {
        var exists = await _db.Users.AsNoTracking().AnyAsync(user => user.Id == userId);
        if (!exists) return NotFound();

        var now = DateTime.UtcNow;
        var setting = await _db.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Key == key);

        if (setting is null)
        {
            setting = new UserSetting
            {
                UserId = userId,
                Key = key,
                Value = request.Value,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.UserSettings.Add(setting);
        }
        else
        {
            setting.Value = request.Value;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();

        return Ok(Map(setting));
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
