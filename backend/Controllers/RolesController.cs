using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize(Policy = "Admin")]
public class RolesController : ControllerBase
{
    private readonly AuthService _svc;
    public RolesController(AuthService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetRoles()
        => Ok(await _svc.GetAllRolesAsync());
}
