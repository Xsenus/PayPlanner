using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
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
        var directAttachments = MessengerDirectAttachmentPolicy.GetStatus(_configuration);
        return Ok(new
        {
            Enabled = enabled,
            LinkLifetimeHours = Math.Clamp(
                _configuration.GetValue("MessengerDocumentDelivery:LinkLifetimeHours", 24),
                1,
                168),
            DirectAttachments = directAttachments,
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
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("financial-documents/{documentType}/{documentId:int}/prepare")]
    [RequestSizeLimit(26L * 1024 * 1024)]
    public async Task<IActionResult> PrepareFinancialDocument(
        string documentType,
        int documentId,
        [FromForm] IFormFile? file,
        [FromForm] int recipientBindingId,
        [FromForm] string? message,
        [FromForm] bool confirmRecipientAndDocument,
        CancellationToken ct)
    {
        if (!confirmRecipientAndDocument)
        {
            return BadRequest(new { message = "Подтвердите выбранный документ и получателя." });
        }
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Сформированный PDF не передан." });
        }

        var normalizedType = documentType.Trim().ToLowerInvariant();
        var binding = await _db.MessengerRecipientBindings
            .AsNoTracking()
            .Where(item => item.Id == recipientBindingId && item.IsActive)
            .Select(item => new { item.ClientId })
            .FirstOrDefaultAsync(ct);
        if (binding?.ClientId is null)
        {
            return BadRequest(new { message = "Подтверждённый контакт получателя не найден." });
        }

        var clientId = binding.ClientId.Value;
        var source = await ResolveFinancialDocumentSourceAsync(normalizedType, documentId, clientId, ct);
        if (source is null)
        {
            return BadRequest(new { message = "Документ не найден или не принадлежит клиенту выбранного получателя." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var prepared = await _deliveryService.PrepareGeneratedDocumentAsync(
                User.GetUserId() ?? 0,
                normalizedType,
                documentId,
                1,
                clientId,
                source.FileName,
                file.ContentType,
                stream,
                file.Length,
                recipientBindingId,
                message,
                ct);
            return Ok(ToPreparedResponse(prepared));
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

    private async Task<FinancialDocumentSource?> ResolveFinancialDocumentSourceAsync(
        string documentType,
        int documentId,
        int clientId,
        CancellationToken ct)
    {
        if (documentType == "invoice")
        {
            var number = await _db.Payments
                .AsNoTracking()
                .Where(item => item.Id == documentId
                    && (item.ClientId == clientId || item.ExpenseClientId == clientId))
                .Select(item => item.Invoice != null ? item.Invoice.Number : item.Account)
                .FirstOrDefaultAsync(ct);
            return string.IsNullOrWhiteSpace(number) ? null : new($"invoice-{SanitizeFilePart(number)}.pdf");
        }
        if (documentType == "act")
        {
            var number = await _db.Acts.AsNoTracking()
                .Where(item => item.Id == documentId && item.ClientId == clientId)
                .Select(item => item.Number)
                .FirstOrDefaultAsync(ct);
            return string.IsNullOrWhiteSpace(number) ? null : new($"act-{SanitizeFilePart(number)}.pdf");
        }
        if (documentType == "contract")
        {
            var number = await _db.Contracts.AsNoTracking()
                .Where(item => item.Id == documentId
                    && item.ClientContracts.Any(link => link.ClientId == clientId))
                .Select(item => item.Number)
                .FirstOrDefaultAsync(ct);
            return string.IsNullOrWhiteSpace(number) ? null : new($"contract-{SanitizeFilePart(number)}.pdf");
        }
        return null;
    }

    private static object ToPreparedResponse(PreparedMessengerDocumentDelivery prepared)
    {
        var delivery = prepared.Delivery;
        return new
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
        };
    }

    private static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }

    private sealed record FinancialDocumentSource(string FileName);
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
    [EnableRateLimiting(PublicAccessRateLimitPolicies.MessengerDocumentDownload)]
    public async Task<IActionResult> Download(
        Guid publicId,
        [FromQuery, StringLength(512, MinimumLength = 1)] string? token,
        CancellationToken ct)
    {
        var download = await _deliveryService.ConsumeDownloadAsync(publicId, token ?? string.Empty, ct);
        if (download is null)
        {
            return NotFound();
        }

        return CreateDownloadResult(download);
    }

    /// <summary>
    /// Выдаёт документ по токену из тела POST. Новые публичные страницы используют
    /// именно этот маршрут, чтобы секрет не сохранялся в URL и access-журналах.
    /// </summary>
    [HttpPost("{publicId:guid}/download")]
    [EnableRateLimiting(PublicAccessRateLimitPolicies.MessengerDocumentDownload)]
    [RequestSizeLimit(4 * 1024)]
    public async Task<IActionResult> DownloadSecurely(
        Guid publicId,
        [FromBody] MessengerDocumentDownloadRequest request,
        CancellationToken ct)
    {
        var download = await _deliveryService.ConsumeDownloadAsync(publicId, request.Token, ct);
        return download is null ? NotFound() : CreateDownloadResult(download);
    }

    [HttpPost("callbacks/{provider}")]
    [EnableRateLimiting(PublicAccessRateLimitPolicies.MessengerDocumentCallback)]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Callback(
        [StringLength(32, MinimumLength = 1)] string provider,
        [FromBody] MessengerDocumentDeliveryCallbackRequest request,
        CancellationToken ct)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider is not (MessengerDocumentChannels.Telegram
            or MessengerDocumentChannels.Max
            or MessengerDocumentChannels.WhatsAppBusiness))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.ProviderMessageId))
        {
            return BadRequest(new { message = "Идентификатор сообщения провайдера обязателен." });
        }

        var configurationProvider = provider switch
        {
            MessengerDocumentChannels.Telegram => "Telegram",
            MessengerDocumentChannels.Max => "Max",
            MessengerDocumentChannels.WhatsAppBusiness => "WhatsAppBusiness",
            _ => "WhatsAppBusiness",
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
        var delivery = await _db.MessengerDocumentDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == provider && item.ProviderMessageId == messageId)
            .Select(item => new { item.Id, item.Status, item.ReadAt, item.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (delivery is null)
        {
            return NotFound();
        }

        var receivedAt = DateTime.UtcNow;
        var occurredAt = request.OccurredAt?.ToUniversalTime() ?? receivedAt;
        if (occurredAt > receivedAt.AddMinutes(5) || occurredAt < delivery.CreatedAt.AddMinutes(-5))
        {
            occurredAt = receivedAt;
        }

        int updated;
        if (normalizedStatus == MessengerDocumentDeliveryStatuses.Delivered)
        {
            updated = await _db.MessengerDocumentDeliveries
                .Where(item => item.Id == delivery.Id && item.ReadAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, MessengerDocumentDeliveryStatuses.Delivered)
                        .SetProperty(item => item.DeliveredAt, item => item.DeliveredAt ?? occurredAt)
                        .SetProperty(item => item.UpdatedAt, receivedAt),
                    ct);
        }
        else if (normalizedStatus == MessengerDocumentDeliveryStatuses.Read)
        {
            updated = await _db.MessengerDocumentDeliveries
                .Where(item => item.Id == delivery.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, MessengerDocumentDeliveryStatuses.Read)
                        .SetProperty(item => item.DeliveredAt, item => item.DeliveredAt ?? occurredAt)
                        .SetProperty(item => item.ReadAt, item => item.ReadAt ?? occurredAt)
                        .SetProperty(item => item.UpdatedAt, receivedAt),
                    ct);
        }
        else
        {
            var safeError = string.IsNullOrWhiteSpace(request.Error)
                ? "Provider callback reported failure."
                : request.Error.Trim()[..Math.Min(request.Error.Trim().Length, 2000)];
            // Условие по ReadAt не позволяет запоздалому или параллельному failure
            // понизить уже подтверждённый провайдером статус чтения.
            updated = await _db.MessengerDocumentDeliveries
                .Where(item => item.Id == delivery.Id && item.ReadAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, MessengerDocumentDeliveryStatuses.Failed)
                        .SetProperty(item => item.FailedAt, item => item.FailedAt ?? occurredAt)
                        .SetProperty(item => item.LastError, safeError)
                        .SetProperty(item => item.UpdatedAt, receivedAt),
                    ct);
        }

        var responseStatus = updated == 1
            ? normalizedStatus
            : await _db.MessengerDocumentDeliveries
                .AsNoTracking()
                .Where(item => item.Id == delivery.Id)
                .Select(item => item.Status)
                .SingleAsync(ct);
        return Ok(new { delivery.Id, Status = responseStatus });
    }

    /// <summary>Формирует ответ без кэширования, range-повторов и передачи referrer.</summary>
    private IActionResult CreateDownloadResult(MessengerDocumentDownload download)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "no-store, private";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        return download.Content is { } content
            ? new FileStreamResult(content, download.ContentType) { FileDownloadName = download.FileName, EnableRangeProcessing = false }
            : PhysicalFile(download.FullPath, download.ContentType, download.FileName, enableRangeProcessing: false);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(left));
        var rightHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(right));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
