using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Policy = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AuthService _svc;

    public UsersController(AuthService svc) => _svc = svc;

    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> ApproveUser(int id)
    {
        var s = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(s, out var approverId))
            return Unauthorized();

        var ok = await _svc.ApproveUserAsync(id, approverId);
        return ok ? Ok(new { approved = true }) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        var dto = await _svc.CreateUserAsync(req);
        return dto is null
            ? Conflict(new { message = "Пользователь с таким email уже существует." })
            : Created($"/api/users/{dto.Id}", dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
        => await _svc.DeleteUserAsync(id) ? NoContent() : NotFound();

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await _svc.GetAllRolesAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetUser(int id)
        => (await _svc.GetUserByIdAsync(id)) is { } u ? Ok(u) : NotFound();

    [HttpGet]
    public async Task<IActionResult> GetUsers([FromQuery] string? status)
        => Ok(await _svc.GetAllUsersAsync(status));

    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> RejectUser(int id, [FromBody] RejectUserRequest? request)
        => await _svc.RejectUserAsync(id, request?.Reason) ? Ok(new { rejected = true }) : NotFound();

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
        => (await _svc.UpdateUserAsync(id, req)) is { } u ? Ok(u) : NotFound();
}
