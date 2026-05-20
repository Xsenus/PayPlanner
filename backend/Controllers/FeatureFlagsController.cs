using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/features")]
public sealed class FeatureFlagsController : ControllerBase
{
    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _options;

    public FeatureFlagsController(IOptionsSnapshot<AppFeatureFlagsOptions> options)
    {
        _options = options;
    }

    [HttpGet]
    [AllowAnonymous]
    public ActionResult<AppFeatureFlagsDto> Get()
    {
        return Ok(AppFeatureFlagsDto.FromOptions(_options.Value));
    }
}
