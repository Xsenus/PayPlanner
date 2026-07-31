using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
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
    private readonly IConfiguration _configuration;

    public MessengerDocumentDeliveriesController(
        PaymentContext db,
        IMessengerDocumentDeliveryService deliveryService,
        IConfiguration configuration)
    {
        _db = db;
        _deliveryService = deliveryService;
        _configuration = configuration;
    }

    [HttpGet("configuration")]
    public IActionResult GetConfiguration()
    {
        var enabled = _configuration.GetValue("MessengerDocumentDelivery:Enabled", false);
        return Ok(new
        {
            Enabled = enabled,
            LinkLifetimeHours = Math.Clamp(
                _configuration.GetValue("MessengerDocumentDelivery:LinkLifetimeHours", 24),
                1,
                168),
            Providers = new[]
            {
                ProviderState(MessengerDocumentChannels.Telegram),
                ProviderState(MessengerDocumentChannels.Max),
                ProviderState(MessengerDocumentChannels.WhatsAppBusiness),
            },
        });
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

    [HttpPost("{deliveryId:int}/queue")]
    public async Task<IActionResult> Queue(int deliveryId, CancellationToken ct)
    {
        try
        {
            var delivery = await _deliveryService.QueueAsync(deliveryId, ct);
            return Ok(new
            {
                delivery.Id,
                delivery.Status,
                delivery.QueuedAt,
                delivery.NextAttemptAt,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private object ProviderState(string channel) => new
    {
        Channel = channel,
        Enabled = _configuration.GetValue(
            $"MessengerDocumentDelivery:Providers:{ProviderConfigurationName(channel)}:Enabled",
            false),
    };

    private static string ProviderConfigurationName(string channel) => channel switch
    {
        MessengerDocumentChannels.Telegram => "Telegram",
        MessengerDocumentChannels.Max => "Max",
        MessengerDocumentChannels.WhatsAppBusiness => "WhatsAppBusiness",
        _ => channel,
    };
}

[ApiController]
[Route("api/public/messenger-document-deliveries")]
[AllowAnonymous]
public sealed class PublicMessengerDocumentDeliveriesController : ControllerBase
{
    private readonly IMessengerDocumentDeliveryService _deliveryService;
    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;

    public PublicMessengerDocumentDeliveriesController(
        IMessengerDocumentDeliveryService deliveryService,
        PaymentContext db,
        IConfiguration configuration)
    {
        _deliveryService = deliveryService;
        _db = db;
        _configuration = configuration;
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

    [HttpPost("callbacks/{provider}")]
    public async Task<IActionResult> Callback(
        string provider,
        [FromBody] MessengerDocumentDeliveryCallbackRequest request,
        CancellationToken ct)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.ProviderMessageId))
        {
            return BadRequest(new { message = "Идентификатор сообщения провайдера обязателен." });
        }

        var configurationProvider = provider switch
        {
            MessengerDocumentChannels.Telegram => "Telegram",
            MessengerDocumentChannels.Max => "Max",
            MessengerDocumentChannels.WhatsAppBusiness => "WhatsAppBusiness",
            _ => provider,
        };
        var expectedSecret = _configuration[$"MessengerDocumentDelivery:Providers:{configurationProvider}:WebhookSecret"];
        var providedSecret = Request.Headers["X-Document-Delivery-Webhook-Secret"].ToString();
        if (string.IsNullOrWhiteSpace(expectedSecret)
            || !FixedTimeEquals(expectedSecret, providedSecret))
        {
            return NotFound();
        }

        var normalizedStatus = request.Status.Trim().ToLowerInvariant();
        if (normalizedStatus is not (MessengerDocumentDeliveryStatuses.Delivered
            or MessengerDocumentDeliveryStatuses.Read
            or MessengerDocumentDeliveryStatuses.Failed))
        {
            return BadRequest(new { message = "Неподдерживаемый статус доставки." });
        }

        var messageId = request.ProviderMessageId.Trim();
        var delivery = await _db.MessengerDocumentDeliveries.FirstOrDefaultAsync(
            item => item.Channel == provider && item.ProviderMessageId == messageId,
            ct);
        if (delivery is null)
        {
            return NotFound();
        }

        var occurredAt = request.OccurredAt?.ToUniversalTime() ?? DateTime.UtcNow;
        if (normalizedStatus == MessengerDocumentDeliveryStatuses.Delivered)
        {
            if (delivery.ReadAt is null)
            {
                delivery.Status = MessengerDocumentDeliveryStatuses.Delivered;
            }
            delivery.DeliveredAt ??= occurredAt;
        }
        else if (normalizedStatus == MessengerDocumentDeliveryStatuses.Read)
        {
            delivery.Status = MessengerDocumentDeliveryStatuses.Read;
            delivery.DeliveredAt ??= occurredAt;
            delivery.ReadAt ??= occurredAt;
        }
        else if (delivery.ReadAt is null)
        {
            delivery.Status = MessengerDocumentDeliveryStatuses.Failed;
            delivery.FailedAt = occurredAt;
            delivery.LastError = string.IsNullOrWhiteSpace(request.Error)
                ? "Provider callback reported failure."
                : request.Error.Trim()[..Math.Min(request.Error.Trim().Length, 2000)];
        }
        delivery.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { delivery.Id, delivery.Status });
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(left));
        var rightHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(right));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
