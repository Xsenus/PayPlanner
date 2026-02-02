using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using System.Security.Claims;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/user-settings")]
[Authorize]
public class UserSettingsController : ControllerBase
{
    private readonly PaymentContext _db;

    public UserSettingsController(PaymentContext db) => _db = db;

    [HttpGet("{key}")]
    public async Task<ActionResult<UserSettingDto>> Get(string key)
    {
        var userId = GetUserId(User);
        if (userId is null) return Unauthorized();

        var setting = await _db.UserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId.Value && s.Key == key);

        if (setting is null) return NotFound();

        return Ok(Map(setting));
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<UserSettingDto>> Upsert(string key, [FromBody] UpsertUserSettingRequest request)
    {
        var userId = GetUserId(User);
        if (userId is null) return Unauthorized();

        var now = DateTime.UtcNow;
        var setting = await _db.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId.Value && s.Key == key);

        if (setting is null)
        {
            setting = new UserSetting
            {
                UserId = userId.Value,
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
