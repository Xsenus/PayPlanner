using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/auth/admin")]
[Authorize(Policy = "Admin")]
public class AdminUsersController : ControllerBase
{
    private readonly AuthService _svc;

    public AdminUsersController(AuthService svc) => _svc = svc;

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] string? status)
        => Ok(await _svc.GetAllUsersAsync(status));

    [HttpGet("users/{id:int}")]
    public async Task<IActionResult> GetUser(int id)
    {
        var user = await _svc.GetUserByIdAsync(id);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        var dto = await _svc.CreateUserAsync(req);
        return dto is null
            ? Conflict(new { message = "РџРѕР»СЊР·РѕРІР°С‚РµР»СЊ СЃ С‚Р°РєРёРј email СѓР¶Рµ СЃСѓС‰РµСЃС‚РІСѓРµС‚." })
            : Created($"/api/auth/users/{dto.Id}", dto);
    }

    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var dto = await _svc.UpdateUserAsync(id, req, actorUserId.Value);
            return dto is null ? NotFound() : Ok(dto);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "ProtectedUserManagedOnlyBySelf"
            || ex.Message == "ProtectedUserEmailChangeForbidden"
            || ex.Message == "ProtectedUserAdministrativeFieldsLocked"
            || ex.Message == "ProtectedUserEmailReserved")
        {
            return Conflict(new { message = "Изменение системного администратора ограничено: этот пользователь управляет собой только самостоятельно, а его email зарезервирован." });
        }
    }

    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return (await _svc.DeleteUserAsync(id, actorUserId.Value)) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "ProtectedUserCannotBeDeleted")
        {
            return Conflict(new { message = "РЎРёСЃС‚РµРјРЅРѕРіРѕ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РЅРµР»СЊР·СЏ СѓРґР°Р»РёС‚СЊ." });
        }
    }

    [HttpPost("users/{id:int}/approve")]
    public async Task<IActionResult> ApproveUser(int id)
    {
        var s = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(s, out var approverId))
        {
            return Unauthorized();
        }

        var ok = await _svc.ApproveUserAsync(id, approverId);
        return ok ? Ok(new { approved = true }) : NotFound();
    }

    [HttpPost("users/{id:int}/reject")]
    public async Task<IActionResult> RejectUser(int id, [FromBody] RejectUserRequest? request)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var ok = await _svc.RejectUserAsync(id, actorUserId.Value, request?.Reason);
            return ok ? Ok(new { rejected = true }) : NotFound();
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "ProtectedUserManagedOnlyBySelf"
            || ex.Message == "ProtectedUserCannotBeDeleted")
        {
            return Conflict(new { message = "РЎРёСЃС‚РµРјРЅРѕРіРѕ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РЅРµР»СЊР·СЏ РѕС‚РєР»РѕРЅРёС‚СЊ РёР»Рё СѓРґР°Р»РёС‚СЊ." });
        }
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await _svc.GetAllRolesAsync());
}

