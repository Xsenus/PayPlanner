using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/calendar-sync")]
public sealed class CalendarSyncController : ControllerBase
{
    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _featureFlags;

    public CalendarSyncController(IOptionsSnapshot<AppFeatureFlagsOptions> featureFlags)
    {
        _featureFlags = featureFlags;
    }

    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<CalendarSyncProviderDto>> GetProviders()
        => Ok(CalendarSyncProviderRegistry.BuildProviders(_featureFlags.Value.Legal.ExternalCalendarSync));
}
