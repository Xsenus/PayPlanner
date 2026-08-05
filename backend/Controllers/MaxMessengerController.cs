using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/max")]
[Authorize]
public sealed class MaxMessengerController(
    IMaxWebhookService service,
    IMaxOutboxService outbox,
    IMaxCorrespondenceService correspondence,
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
            request.ActionUrl,
            request.NotificationType), ct);
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

    [HttpPost("messages/{publicId:guid}/retry")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> RetryMessage(Guid publicId, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var actorName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Администратор";
        try
        {
            return await outbox.RetryAsync(publicId, userId, actorName, ct) ? Accepted() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpGet("correspondence")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GetCorrespondence(
        [FromQuery] string? status = null,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
        => Ok(await correspondence.GetAsync(status, take, ct));

    [HttpPost("correspondence/{publicId:guid}/route")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> RouteCorrespondence(
        Guid publicId,
        [FromBody] RouteMaxCorrespondenceRequest request,
        CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        try
        {
            return await correspondence.RouteAsync(publicId, request.ClientCaseId, userId, ct)
                ? NoContent()
                : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}

public sealed class RouteMaxCorrespondenceRequest
{
    public int ClientCaseId { get; set; }
}

public sealed class QueueMaxMessageRequest
{
    public int RecipientUserId { get; set; }
    public string Scenario { get; set; } = MaxMessengerScenarios.Notifications;
    public string EventKey { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? ActionText { get; set; }
    public string? ActionUrl { get; set; }
    public string? NotificationType { get; set; } = UserNotificationTypes.SystemAdminWarning;
}

[ApiController]
[Route("api/integrations/max/webhook")]
[AllowAnonymous]
public sealed class MaxWebhookController(IMaxWebhookService service) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(AnonymousIntegrationRateLimitPolicies.Webhook)]
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
