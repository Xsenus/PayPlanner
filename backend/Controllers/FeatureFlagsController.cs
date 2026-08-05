using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/features")]
public sealed class FeatureFlagsController : ControllerBase
{
    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _options;
    private readonly ILegalIntegrationDiagnosticsService? _legalIntegrationDiagnostics;

    public FeatureFlagsController(
        IOptionsSnapshot<AppFeatureFlagsOptions> options,
        ILegalIntegrationDiagnosticsService? legalIntegrationDiagnostics = null)
    {
        _options = options;
        _legalIntegrationDiagnostics = legalIntegrationDiagnostics;
    }

    [HttpGet]
    [AllowAnonymous]
    [EnableRateLimiting(AnonymousIntegrationRateLimitPolicies.PublicMetadata)]
    public ActionResult<AppFeatureFlagsDto> Get()
    {
        return Ok(AppFeatureFlagsDto.FromOptions(_options.Value));
    }

    [HttpGet("legal-integrations/diagnostics")]
    [Authorize(Roles = "admin")]
    public ActionResult<LegalIntegrationDiagnosticsDto> GetLegalIntegrationDiagnostics()
    {
        return Ok(_legalIntegrationDiagnostics?.GetDiagnostics()
            ?? LegalIntegrationDiagnosticsService.BuildDiagnostics(_options.Value));
    }
}
