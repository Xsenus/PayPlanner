using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InstallmentsController : ControllerBase
{
    private readonly InstallmentService _svc;
    public InstallmentsController(InstallmentService svc) => _svc = svc;

    [HttpPost("calc")]
    public IActionResult Calc([FromBody] InstallmentRequest request)
        => Ok(_svc.CalculateInstallment(request));
}
