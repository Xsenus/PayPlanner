using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize(Policy = "Admin")]
public class NotificationsController : ControllerBase
{
    private readonly PaymentContext _context;
    private readonly INotificationService _notificationService;
    private readonly ITelegramBotUpdateService _telegramBotUpdateService;
    private readonly IPasswordHasher<User> _passwordHasher;

    public NotificationsController(
        PaymentContext context,
        INotificationService notificationService,
        ITelegramBotUpdateService telegramBotUpdateService,
        IPasswordHasher<User> passwordHasher)
    {
        _context = context;
        _notificationService = notificationService;
        _telegramBotUpdateService = telegramBotUpdateService;
        _passwordHasher = passwordHasher;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<NotificationSettingsDto>> GetSettings(CancellationToken ct)
        => Ok(await _notificationService.GetSettingsAsync(ct));

    [HttpPut("settings/email")]
    public async Task<ActionResult<NotificationSettingsDto>> UpdateEmailSettings(
        [FromBody] UpdateEmailNotificationSettingsRequest request,
        CancellationToken ct)
        => Ok(await _notificationService.UpdateEmailSettingsAsync(request, ct));

    [HttpPut("settings/telegram")]
    public async Task<ActionResult<NotificationSettingsDto>> UpdateTelegramSettings(
        [FromBody] UpdateTelegramNotificationSettingsRequest request,
        CancellationToken ct)
        => Ok(await _notificationService.UpdateTelegramSettingsAsync(request, ct));

    [HttpPost("senders")]
    public async Task<ActionResult<NotificationSettingsDto>> CreateSender(
        [FromBody] UpsertNotificationSenderRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _notificationService.UpsertSenderAsync(request, cancellationToken: ct));
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderChannelInvalid")
        {
            return BadRequest(new { message = "Укажите канал email или telegram." });
        }
    }

    [HttpPut("senders/{id:int}")]
    public async Task<ActionResult<NotificationSettingsDto>> UpdateSender(
        int id,
        [FromBody] UpsertNotificationSenderRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _notificationService.UpsertSenderAsync(request, id, ct));
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderChannelInvalid")
        {
            return BadRequest(new { message = "Укажите канал email или telegram." });
        }
    }

    [HttpDelete("senders/{id:int}")]
    public async Task<IActionResult> DeleteSender(int id, CancellationToken ct)
        => await _notificationService.DeleteSenderAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("senders/{id:int}/telegram/webhook/sync")]
    public async Task<ActionResult<TelegramWebhookSyncResultDto>> SyncTelegramWebhook(int id, CancellationToken ct)
    {
        try
        {
            var result = await _telegramBotUpdateService.SyncWebhookAsync(id, Request, ct);
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "TelegramBotTokenMissing")
        {
            return BadRequest(new TelegramWebhookSyncResultDto
            {
                Success = false,
                Message = "У Telegram-бота не указан token."
            });
        }
    }

    [HttpDelete("senders/{id:int}/telegram/webhook")]
    public async Task<ActionResult<TelegramWebhookSyncResultDto>> DeleteTelegramWebhook(int id, CancellationToken ct)
    {
        try
        {
            var result = await _telegramBotUpdateService.DeleteWebhookAsync(id, ct);
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "TelegramBotTokenMissing")
        {
            return BadRequest(new TelegramWebhookSyncResultDto
            {
                Success = false,
                Message = "У Telegram-бота не указан token."
            });
        }
    }

    [HttpPost("senders/{id:int}/telegram/profile/sync")]
    public async Task<ActionResult<TelegramBotProfileSyncResultDto>> SyncTelegramBotProfile(int id, CancellationToken ct)
    {
        try
        {
            var result = await _telegramBotUpdateService.SyncBotProfileAsync(id, ct);
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "TelegramBotTokenMissing")
        {
            return BadRequest(new TelegramBotProfileSyncResultDto
            {
                Success = false,
                Message = "У Telegram-бота не указан token."
            });
        }
    }

    [HttpPut("senders/{id:int}/telegram/profile")]
    public async Task<ActionResult<TelegramBotProfileSyncResultDto>> ApplyTelegramBotProfile(
        int id,
        [FromBody] UpdateTelegramBotProfileRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _telegramBotUpdateService.ApplyBotProfileAsync(id, request, ct);
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "TelegramBotTokenMissing")
        {
            return BadRequest(new TelegramBotProfileSyncResultDto
            {
                Success = false,
                Message = "У Telegram-бота не указан token."
            });
        }
    }

    [HttpGet("senders/{id:int}/telegram/photo")]
    public async Task<IActionResult> GetTelegramBotProfilePhoto(int id, CancellationToken ct)
    {
        try
        {
            var photo = await _telegramBotUpdateService.GetBotProfilePhotoAsync(id, ct);
            return photo is null
                ? NotFound()
                : File(photo.Bytes, photo.ContentType, photo.FileName);
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "TelegramBotTokenMissing")
        {
            return BadRequest(new { message = "У Telegram-бота не указан token." });
        }
    }

    [AllowAnonymous]
    [HttpPost("telegram/{senderId:int}/webhook")]
    public async Task<IActionResult> TelegramWebhook(
        int senderId,
        [FromBody] JsonDocument update,
        CancellationToken ct)
    {
        try
        {
            await _telegramBotUpdateService.HandleWebhookAsync(senderId, update, Request, ct);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationSenderNotFound")
        {
            return NotFound();
        }
    }

    [HttpGet("deliveries")]
    public async Task<ActionResult<PagedResponse<NotificationDeliveryDto>>> GetDeliveries(
        [FromQuery] string? channel,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _context.NotificationDeliveries.AsNoTracking();
        var normalizedChannel = NormalizeChannel(channel);
        if (!string.IsNullOrWhiteSpace(normalizedChannel))
        {
            query = query.Where(item => item.Channel == normalizedChannel);
        }

        var normalizedStatus = status?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedStatus))
        {
            query = query.Where(item => item.Status == normalizedStatus);
        }

        var total = await query.CountAsync(ct);
        var deliveries = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<NotificationDeliveryDto>
        {
            Items = deliveries.Select(MapDelivery).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpPost("deliveries/{id:int}/retry")]
    public async Task<ActionResult<IReadOnlyList<NotificationDeliveryDto>>> RetryDelivery(int id, CancellationToken ct)
    {
        try
        {
            var deliveries = await _notificationService.RetryDeliveryAsync(id, ct);
            return Ok(deliveries.Select(MapDelivery).ToList());
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationDeliveryNotFound")
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationDeliveryAlreadySent")
        {
            return BadRequest(new { message = "Уведомление уже было успешно отправлено." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationDeliveryPending")
        {
            return BadRequest(new { message = "Уведомление еще находится в обработке." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationDeliveryChannelInvalid")
        {
            return BadRequest(new { message = "Повторная отправка доступна только для email и Telegram." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "NotificationDeliveryRecipientMissing")
        {
            return BadRequest(new { message = "Не удалось определить получателя для повторной отправки." });
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult<SendNotificationTestResponseDto>> SendTest(
        [FromBody] SendNotificationTestRequest request,
        CancellationToken ct)
    {
        var channel = NormalizeChannel(request.Channel);
        if (channel is not (NotificationChannels.Email or NotificationChannels.Telegram))
        {
            return BadRequest(new { message = "Укажите канал email или telegram." });
        }

        var message = new NotificationMessage(
            string.IsNullOrWhiteSpace(request.Subject) ? "Тестовое уведомление PayPlanner" : request.Subject.Trim(),
            string.IsNullOrWhiteSpace(request.Message) ? "Это тестовое уведомление PayPlanner." : request.Message.Trim());

        IReadOnlyList<NotificationDelivery> deliveries;
        if (request.UserId.HasValue)
        {
            deliveries = await _notificationService.SendToUserAsync(
                request.UserId.Value,
                message,
                [channel],
                $"test:user:{request.UserId.Value}:{Guid.NewGuid():N}",
                ct,
                request.SenderId);
        }
        else
        {
            var recipient = BuildManualRecipient(request, channel);
            if (recipient is null)
            {
                return BadRequest(new { message = "Укажите пользователя, email или Telegram chat id." });
            }

            deliveries = await _notificationService.SendAsync(
                recipient,
                message,
                [channel],
                $"test:manual:{Guid.NewGuid():N}",
                ct,
                request.SenderId);
        }

        var delivery = deliveries.LastOrDefault();
        return Ok(new SendNotificationTestResponseDto
        {
            Success = delivery?.Status == NotificationDeliveryStatus.Sent,
            Channel = channel,
            Recipient = delivery?.RecipientAddress ?? string.Empty,
            Status = delivery?.Status ?? NotificationDeliveryStatus.Skipped,
            Error = delivery?.Error
        });
    }

    [HttpPost("users/{id:int}/issue-password")]
    public async Task<ActionResult<IssuePasswordResponseDto>> IssuePassword(
        int id,
        [FromBody] IssuePasswordRequest? request,
        CancellationToken ct)
    {
        var user = await _context.Users
            .Include(item => item.ExternalIdentities)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        var password = AuthService.CreateGeneratedPassword();
        user.PasswordHash = _passwordHasher.HashPassword(user, password);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        var channels = NormalizeChannels(request?.Channels);
        var deliveries = await _notificationService.NotifyPasswordIssuedAsync(
            user,
            password,
            channels.Count == 0 ? null : channels,
            ct);

        return Ok(new IssuePasswordResponseDto
        {
            UserId = user.Id,
            PasswordChanged = true,
            Password = request?.IncludePasswordInResponse == true ? password : null,
            Deliveries = deliveries.Select(MapDelivery).ToList()
        });
    }

    private static NotificationRecipient? BuildManualRecipient(SendNotificationTestRequest request, string channel)
    {
        if (channel == NotificationChannels.Email)
        {
            var email = request.Email?.Trim();
            return string.IsNullOrWhiteSpace(email)
                ? null
                : new NotificationRecipient(null, email, email, null);
        }

        return request.TelegramChatId.HasValue
            ? new NotificationRecipient(null, request.TelegramChatId.Value.ToString(), null, request.TelegramChatId.Value)
            : null;
    }

    private static List<string> NormalizeChannels(IEnumerable<string>? channels)
        => (channels ?? [])
            .Select(NormalizeChannel)
            .Where(channel => channel is NotificationChannels.Email or NotificationChannels.Telegram)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeChannel(string? channel)
        => (channel ?? string.Empty).Trim().ToLowerInvariant();

    private static NotificationDeliveryDto MapDelivery(NotificationDelivery item)
        => new()
        {
            Id = item.Id,
            EventKey = item.EventKey,
            Channel = item.Channel,
            SenderId = item.NotificationSenderId,
            SenderName = item.SenderName,
            RecipientUserId = item.RecipientUserId,
            RecipientAddress = item.RecipientAddress,
            Subject = item.Subject,
            Status = item.Status,
            Error = item.Error,
            CreatedAt = item.CreatedAt,
            SentAt = item.SentAt
        };
}
