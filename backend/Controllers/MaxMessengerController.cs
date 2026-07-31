using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/max")]
[Authorize]
public sealed class MaxMessengerController(
    IMaxWebhookService service,
    IMaxOutboxService outbox,
    PaymentContext db) : ControllerBase
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

    [HttpPost("messages")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> QueueMessage([FromBody] QueueMaxMessageRequest request, CancellationToken ct)
    {
        var message = await outbox.QueueAsync(new MaxQueueRequest(
            request.RecipientUserId,
            request.Scenario,
            request.EventKey,
            request.Text,
            request.ActionText,
            request.ActionUrl), ct);
        return Accepted(new { message.PublicId, message.Status, message.CreatedAt });
    }

    [HttpGet("messages")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GetMessages([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var items = await db.MaxOutboxMessages.AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(item => new
            {
                item.PublicId,
                item.EventKey,
                item.Scenario,
                item.RecipientUserId,
                item.BodyPreview,
                item.Status,
                item.AttemptCount,
                item.MaxAttempts,
                item.NextAttemptAt,
                item.LastErrorCode,
                item.CreatedAt,
                item.SentAt
            })
            .ToListAsync(ct);
        return Ok(items);
    }
}

public sealed class QueueMaxMessageRequest
{
    public int RecipientUserId { get; set; }
    public string Scenario { get; set; } = MaxMessengerScenarios.Notifications;
    public string EventKey { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? ActionText { get; set; }
    public string? ActionUrl { get; set; }
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
