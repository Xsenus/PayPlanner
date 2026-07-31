using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/max")]
[Authorize]
public sealed class MaxMessengerController(IMaxWebhookService service) : ControllerBase
{
    [HttpPost("link")]
    public async Task<ActionResult<MaxLinkTokenResult>> CreateLink(CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(await service.CreateEmployeeLinkAsync(userId, ct));
    }

    [HttpDelete("link")]
    public async Task<IActionResult> OptOut(CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return await service.OptOutEmployeeAsync(userId, ct) ? NoContent() : NotFound();
    }
}

[ApiController]
[Route("api/integrations/max/webhook")]
[AllowAnonymous]
public sealed class MaxWebhookController(IMaxWebhookService service) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Receive(
        [FromBody] JsonElement update,
        [FromHeader(Name = "X-Max-Bot-Api-Secret")] string? secret,
        CancellationToken ct)
    {
        var result = await service.ProcessAsync(update.GetRawText(), secret, ct);
        if (result.Accepted) return Ok(new { result.Duplicate, result.Status });
        return result.ErrorCode switch
        {
            "invalid-secret" => Unauthorized(),
            "channel-disabled" => StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => BadRequest(new { result.Status, result.ErrorCode })
        };
    }
}
