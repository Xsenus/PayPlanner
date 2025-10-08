using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using System.Security.Claims;

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
            ? Conflict(new { message = "ѕользователь с таким email уже существует." })
            : Created($"/api/auth/users/{dto.Id}", dto);
    }

    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var dto = await _svc.UpdateUserAsync(id, req);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
        => (await _svc.DeleteUserAsync(id)) ? NoContent() : NotFound();

    [HttpPost("users/{id:int}/approve")]
    public async Task<IActionResult> ApproveUser(int id)
    {
        var s = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(s, out var approverId))
            return Unauthorized();

        var ok = await _svc.ApproveUserAsync(id, approverId);
        return ok ? Ok(new { approved = true }) : NotFound();
    }

    [HttpPost("users/{id:int}/reject")]
    public async Task<IActionResult> RejectUser(int id, [FromBody] RejectUserRequest? request)
    {
        var ok = await _svc.RejectUserAsync(id, request?.Reason);
        return ok ? Ok(new { rejected = true }) : NotFound();
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await _svc.GetAllRolesAsync());
}
