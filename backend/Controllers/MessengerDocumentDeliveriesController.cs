using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/messenger-document-deliveries")]
[Authorize(Policy = "Admin")]
public sealed class MessengerDocumentDeliveriesController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly IMessengerDocumentDeliveryService _deliveryService;

    public MessengerDocumentDeliveriesController(
        PaymentContext db,
        IMessengerDocumentDeliveryService deliveryService)
    {
        _db = db;
        _deliveryService = deliveryService;
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> GetRecipients([FromQuery] int clientId, CancellationToken ct)
    {
        var items = await _db.MessengerRecipientBindings
            .AsNoTracking()
            .Where(item => item.ClientId == clientId && item.IsActive)
            .OrderByDescending(item => item.VerifiedAt)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.ClientId,
                item.ClientPortalAccountId,
                item.Channel,
                item.Provider,
                item.DisplayName,
                item.MaskedAddress,
                item.VerificationStatus,
                item.VerificationMethod,
                item.VerifiedAt,
            })
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("recipients")]
    public async Task<IActionResult> CreateRecipient(
        [FromBody] CreateMessengerRecipientBindingRequest request,
        CancellationToken ct)
    {
        if (!request.ConfirmContactOwnership)
        {
            return BadRequest(new { message = "Подтвердите принадлежность контакта выбранному клиенту или представителю." });
        }

        try
        {
            var item = await _deliveryService.CreateVerifiedBindingAsync(
                User.GetUserId() ?? 0,
                request.ClientId,
                request.ClientPortalAccountId,
                request.Channel,
                request.Provider,
                request.DisplayName,
                request.Address,
                request.VerificationMethod,
                ct);
            return Created($"/api/messenger-document-deliveries/recipients/{item.Id}", new
            {
                item.Id,
                item.ClientId,
                item.ClientPortalAccountId,
                item.Channel,
                item.Provider,
                item.DisplayName,
                item.MaskedAddress,
                item.VerificationStatus,
                item.VerificationMethod,
                item.VerifiedAt,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("case-documents/{documentId:int}/prepare")]
    public async Task<IActionResult> PrepareCaseDocument(
        int documentId,
        [FromBody] PrepareCaseDocumentMessengerDeliveryRequest request,
        CancellationToken ct)
    {
        if (!request.ConfirmRecipientAndDocument)
        {
            return BadRequest(new { message = "Подтвердите выбранный документ и получателя." });
        }

        try
        {
            var prepared = await _deliveryService.PrepareCaseDocumentAsync(
                User.GetUserId() ?? 0,
                documentId,
                request.RecipientBindingId,
                request.Message,
                ct);
            var delivery = prepared.Delivery;
            return Ok(new
            {
                delivery.Id,
                delivery.PublicId,
                delivery.DocumentType,
                delivery.DocumentId,
                delivery.DocumentVersion,
                delivery.OriginalFileName,
                delivery.ChecksumSha256,
                delivery.SizeBytes,
                delivery.Channel,
                delivery.Provider,
                delivery.Status,
                delivery.LinkExpiresAt,
                Token = prepared.Token,
                DownloadPath = prepared.Token is null
                    ? null
                    : $"/api/public/messenger-document-deliveries/{delivery.PublicId:D}/download?token={Uri.EscapeDataString(prepared.Token)}",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetHistory([FromQuery] int? clientId, CancellationToken ct)
    {
        var query = _db.MessengerDocumentDeliveries.AsNoTracking();
        if (clientId.HasValue)
        {
            query = query.Where(item => item.ClientId == clientId);
        }

        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(200)
            .Select(item => new
            {
                item.Id,
                item.PublicId,
                item.ClientId,
                item.ClientCaseId,
                item.DocumentType,
                item.DocumentId,
                item.DocumentVersion,
                item.OriginalFileName,
                item.ChecksumSha256,
                item.SizeBytes,
                item.Channel,
                item.Provider,
                item.Status,
                item.AttemptCount,
                item.LinkExpiresAt,
                item.FirstOpenedAt,
                item.DownloadCount,
                item.CreatedAt,
                Recipient = item.RecipientBinding.MaskedAddress,
            })
            .ToListAsync(ct);
        return Ok(items);
    }
}

[ApiController]
[Route("api/public/messenger-document-deliveries")]
[AllowAnonymous]
public sealed class PublicMessengerDocumentDeliveriesController : ControllerBase
{
    private readonly IMessengerDocumentDeliveryService _deliveryService;

    public PublicMessengerDocumentDeliveriesController(IMessengerDocumentDeliveryService deliveryService)
    {
        _deliveryService = deliveryService;
    }

    [HttpGet("{publicId:guid}/download")]
    public async Task<IActionResult> Download(Guid publicId, [FromQuery] string? token, CancellationToken ct)
    {
        var download = await _deliveryService.ConsumeDownloadAsync(publicId, token ?? string.Empty, ct);
        if (download is null)
        {
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "no-store, private";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        return PhysicalFile(download.FullPath, download.ContentType, download.FileName, enableRangeProcessing: false);
    }
}
