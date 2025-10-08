using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize(Policy = "Admin")]
public class RolesController : ControllerBase
{
    private readonly PaymentContext _db;

    public RolesController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetRoles()
        => Ok(await _db.Roles.OrderBy(r => r.Name).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetRole(int id)
    {
        var role = await _db.Roles.FindAsync(id);
        return role is not null ? Ok(role) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest req)
    {
        if (await _db.Roles.AnyAsync(r => r.Name.ToLower() == req.Name.ToLower()))
            return Conflict(new { message = "Роль с таким именем уже существует." });

        var role = new Role
        {
            Name = req.Name.Trim(),
            Description = req.Description.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        return Created($"/api/roles/{role.Id}", role);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] UpdateRoleRequest req)
    {
        var role = await _db.Roles.FindAsync(id);
        if (role is null) return NotFound();

        if (await _db.Roles.AnyAsync(r => r.Name.ToLower() == req.Name.ToLower() && r.Id != id))
            return Conflict(new { message = "Роль с таким именем уже существует." });

        role.Name = req.Name.Trim();
        role.Description = req.Description.Trim();

        await _db.SaveChangesAsync();
        return Ok(role);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteRole(int id)
    {
        var role = await _db.Roles.FindAsync(id);
        if (role is null) return NotFound();

        if (await _db.Users.AnyAsync(u => u.RoleId == id))
            return BadRequest(new { message = "Невозможно удалить роль, так как она используется пользователями." });

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
