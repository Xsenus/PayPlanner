using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [EnableRateLimiting(AnonymousIntegrationRateLimitPolicies.PublicMetadata)]
    public IActionResult Get() => Ok(new { status = "ok" });
}
