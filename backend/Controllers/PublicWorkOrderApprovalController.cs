using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("public-work-order-approval")]
[Route("api/public/work-order-approvals")]
public sealed class PublicWorkOrderApprovalController : ControllerBase
{
    private readonly IExternalWorkOrderApprovalService _service;

    public PublicWorkOrderApprovalController(IExternalWorkOrderApprovalService service)
    {
        _service = service;
    }

    [HttpPost("{publicId:guid}/open")]
    public async Task<ActionResult<PublicWorkOrderApprovalDto>> Open(
        Guid publicId,
        [FromBody] PublicWorkOrderApprovalOpenRequest? request,
        CancellationToken ct)
    {
        ApplySecurityHeaders();
        try
        {
            return Ok(await _service.OpenAsync(publicId, request?.Token?.Trim() ?? string.Empty, ct));
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpPost("{publicId:guid}/decision")]
    public async Task<ActionResult<PublicWorkOrderApprovalDecisionDto>> Decide(
        Guid publicId,
        [FromBody] PublicWorkOrderApprovalDecisionRequest? request,
        CancellationToken ct)
    {
        ApplySecurityHeaders();
        if (request is null)
        {
            return BadRequest(new { code = "validation_error", message = "Заполните форму подтверждения." });
        }

        try
        {
            return Ok(await _service.DecideAsync(
                publicId,
                request,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                ct));
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpPost("{publicId:guid}/attachments/{attachmentPublicId:guid}/grant")]
    public async Task<ActionResult<PublicWorkOrderAttachmentGrantDto>> CreateAttachmentGrant(
        Guid publicId,
        Guid attachmentPublicId,
        [FromBody] PublicWorkOrderApprovalOpenRequest? request,
        CancellationToken ct)
    {
        ApplySecurityHeaders();
        try
        {
            return Ok(await _service.CreateAttachmentGrantAsync(
                publicId,
                attachmentPublicId,
                request?.Token?.Trim() ?? string.Empty,
                ct));
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpPost("{publicId:guid}/attachments/{attachmentPublicId:guid}/download")]
    public async Task<IActionResult> DownloadAttachment(
        Guid publicId,
        Guid attachmentPublicId,
        [FromBody] PublicWorkOrderAttachmentDownloadRequest? request,
        CancellationToken ct)
    {
        ApplySecurityHeaders();
        try
        {
            var file = await _service.DownloadAttachmentAsync(
                publicId,
                attachmentPublicId,
                request?.GrantToken?.Trim() ?? string.Empty,
                ct);
            return PhysicalFile(file.Path, file.ContentType, file.FileName, enableRangeProcessing: false);
        }
        catch (ExternalWorkOrderApprovalException ex)
        {
            return MapPublicError(ex);
        }
    }

    private void ApplySecurityHeaders()
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    }

    private ActionResult MapPublicError(ExternalWorkOrderApprovalException ex)
        => ex.Code switch
        {
            "validation_error" or "invalid_confirmation" => BadRequest(new { ex.Code, message = ex.Message }),
            "already_decided" => Conflict(new { ex.Code, message = ex.Message }),
            "feature_disabled" or "link_unavailable" or "link_expired" or "attachment_unavailable" => NotFound(new { code = "link_unavailable", message = ex.Message }),
            _ => StatusCode(StatusCodes.Status409Conflict, new { ex.Code, message = ex.Message }),
        };
}

public sealed class PublicWorkOrderAttachmentDownloadRequest
{
    public string? GrantToken { get; set; }
}

public sealed class PublicWorkOrderApprovalOpenRequest
{
    public string? Token { get; set; }
}
