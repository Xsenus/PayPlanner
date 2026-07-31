using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record MaxLinkTokenResult(Guid LinkId, string Payload, string? DeepLinkUrl, DateTime ExpiresAt);
public sealed record MaxWebhookResult(bool Accepted, bool Duplicate, string Status, string? ErrorCode = null);

public interface IMaxWebhookService
{
    Task<MaxLinkTokenResult> CreateEmployeeLinkAsync(int userId, CancellationToken ct);
    Task<MaxLinkTokenResult> CreatePortalLinkAsync(int portalAccountId, CancellationToken ct);
    Task<bool> OptOutEmployeeAsync(int userId, CancellationToken ct);
    Task<bool> OptOutPortalAsync(int portalAccountId, CancellationToken ct);
    Task<MaxWebhookResult> ProcessAsync(string rawJson, string? suppliedSecret, CancellationToken ct);
}

public sealed class MaxWebhookService(
    PaymentContext db,
    IMaxNotificationService settingsService,
    IConfiguration configuration,
    TimeProvider timeProvider) : IMaxWebhookService
{
    private static readonly HashSet<string> AllowedUpdateTypes =
    [
        "bot_started", "bot_stopped", "dialog_removed", "message_created", "message_callback"
    ];

    public async Task<MaxLinkTokenResult> CreateEmployeeLinkAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0 || !await db.Users.AsNoTracking().AnyAsync(item => item.Id == userId && item.IsActive, ct))
            throw new InvalidOperationException("Активный сотрудник не найден.");
        return await CreateLinkAsync(userId, null, ct);
    }

    public async Task<MaxLinkTokenResult> CreatePortalLinkAsync(int portalAccountId, CancellationToken ct)
    {
        if (portalAccountId <= 0 || !await db.ClientPortalAccounts.AsNoTracking()
            .AnyAsync(item => item.Id == portalAccountId && item.IsActive && item.RevokedAt == null, ct))
            throw new InvalidOperationException("Активный аккаунт клиентского кабинета не найден.");
        return await CreateLinkAsync(null, portalAccountId, ct);
    }

    private async Task<MaxLinkTokenResult> CreateLinkAsync(int? userId, int? portalAccountId, CancellationToken ct)
    {
        var settings = await settingsService.GetRuntimeSettingsAsync(ct);
        if (!settings.Ready) throw new InvalidOperationException("Канал MAX выключен или настроен не полностью.");
        var botName = configuration["MaxMessenger:BotUsername"]?.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(botName)) throw new InvalidOperationException("Имя бота MAX не настроено.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldPending = await db.MaxRecipientLinks
            .Where(item => item.UserId == userId && item.ClientPortalAccountId == portalAccountId
                && item.Status == MaxRecipientLinkStatuses.Pending)
            .ToListAsync(ct);
        foreach (var item in oldPending)
        {
            item.Status = MaxRecipientLinkStatuses.Blocked;
            item.UpdatedAt = now;
        }

        var token = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var link = new MaxRecipientLink
        {
            UserId = userId,
            ClientPortalAccountId = portalAccountId,
            Status = MaxRecipientLinkStatuses.Pending,
            LinkTokenHash = Sha256(token),
            LinkTokenExpiresAt = now.AddMinutes(15),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MaxRecipientLinks.Add(link);
        await db.SaveChangesAsync(ct);

        var url = $"https://max.ru/{Uri.EscapeDataString(botName)}?start={Uri.EscapeDataString(token)}";
        return new MaxLinkTokenResult(link.PublicId, token, url, link.LinkTokenExpiresAt);
    }

    public async Task<bool> OptOutEmployeeAsync(int userId, CancellationToken ct)
        => await OptOutAsync(userId, null, ct);

    public async Task<bool> OptOutPortalAsync(int portalAccountId, CancellationToken ct)
        => await OptOutAsync(null, portalAccountId, ct);

    private async Task<bool> OptOutAsync(int? userId, int? portalAccountId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var links = await db.MaxRecipientLinks
            .Where(item => item.UserId == userId && item.ClientPortalAccountId == portalAccountId
                           && (item.Status == MaxRecipientLinkStatuses.Verified
                               || item.Status == MaxRecipientLinkStatuses.Pending))
            .ToListAsync(ct);
        foreach (var link in links)
        {
            link.Status = MaxRecipientLinkStatuses.OptedOut;
            link.OptedOutAt = now;
            link.UpdatedAt = now;
        }
        if (links.Count > 0) await db.SaveChangesAsync(ct);
        return links.Count > 0;
    }

    public async Task<MaxWebhookResult> ProcessAsync(string rawJson, string? suppliedSecret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        if (Encoding.UTF8.GetByteCount(rawJson) > 64 * 1024)
            return new(false, false, "rejected", "payload-too-large");

        var settings = await settingsService.GetRuntimeSettingsAsync(ct);
        if (!settings.Ready)
            return new(false, false, "disabled", "channel-disabled");
        if (!FixedTimeEquals(settings.WebhookSecret, suppliedSecret))
            return new(false, false, "rejected", "invalid-secret");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return new(false, false, "rejected", "invalid-json");
        }

        using (document)
        {
            var root = document.RootElement;
            var updateType = ReadString(root, "update_type")?.Trim().ToLowerInvariant();
            if (updateType is null || !AllowedUpdateTypes.Contains(updateType))
                return new(false, false, "ignored", "unsupported-update");
            if (!root.TryGetProperty("timestamp", out var timestampElement)
                || !timestampElement.TryGetInt64(out var timestamp))
                return new(false, false, "rejected", "timestamp-missing");

            var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (occurredAt < now.AddMinutes(-10) || occurredAt > now.AddMinutes(2))
                return new(false, false, "rejected", "timestamp-out-of-window");

            var fingerprint = Sha256(rawJson);
            if (await db.MaxInboundEvents.AsNoTracking().AnyAsync(item => item.Fingerprint == fingerprint, ct))
                return new(true, true, "duplicate");

            var maxUserId = ReadInt64(root, "user", "user_id") ?? ReadInt64(root, "message", "sender", "user_id");
            var chatId = ReadInt64(root, "chat_id") ?? ReadInt64(root, "message", "recipient", "chat_id");
            var messageId = ReadString(root, "message", "body", "mid");
            var callbackId = ReadString(root, "callback", "callback_id");
            var inbound = new MaxInboundEvent
            {
                Fingerprint = fingerprint,
                UpdateType = updateType,
                MaxUserId = maxUserId,
                MaxChatId = chatId,
                ExternalMessageId = Truncate(messageId, 256),
                CallbackId = Truncate(callbackId, 256),
                PayloadHash = Sha256(ReadString(root, "payload") ?? string.Empty),
                ProcessingStatus = "recorded",
                OccurredAt = occurredAt,
                ReceivedAt = now,
                ExpiresAt = now.AddDays(Math.Clamp(settings.TechnicalPayloadRetentionDays, 1, 90))
            };

            if (updateType == "bot_started")
            {
                inbound.ProcessingStatus = await VerifyLinkAsync(
                    ReadString(root, "payload"), maxUserId, chatId, now, ct);
            }
            else if (updateType is "bot_stopped" or "dialog_removed")
            {
                inbound.ProcessingStatus = await BlockLinksAsync(maxUserId, now, ct);
            }
            else if (updateType == "message_callback")
            {
                // A callback is recorded for audit only. It never changes a legally significant object.
                inbound.ProcessingStatus = "callback-recorded-no-decision";
            }
            else if (updateType == "message_created")
            {
                inbound.ProcessingStatus = settings.CorrespondenceEnabled
                    ? "correspondence-pending-manual-routing"
                    : "correspondence-disabled";
            }

            db.MaxInboundEvents.Add(inbound);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(inbound).State = EntityState.Detached;
                if (await db.MaxInboundEvents.AsNoTracking().AnyAsync(item => item.Fingerprint == fingerprint, ct))
                    return new(true, true, "duplicate");
                throw;
            }

            await db.MaxInboundEvents
                .Where(item => item.ExpiresAt < now)
                .ExecuteDeleteAsync(ct);
            return new(true, false, inbound.ProcessingStatus);
        }
    }

    private async Task<string> VerifyLinkAsync(string? token, long? maxUserId, long? chatId, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || !maxUserId.HasValue || maxUserId <= 0 || !chatId.HasValue || chatId <= 0)
            return "link-invalid";
        var tokenHash = Sha256(token.Trim());
        var link = await db.MaxRecipientLinks.FirstOrDefaultAsync(
            item => item.LinkTokenHash == tokenHash && item.Status == MaxRecipientLinkStatuses.Pending,
            ct);
        if (link is null || link.LinkTokenExpiresAt < now)
            return "link-expired-or-unknown";

        var conflict = await db.MaxRecipientLinks.AsNoTracking().AnyAsync(
            item => item.Id != link.Id
                    && item.MaxUserId == maxUserId
                    && item.Status == MaxRecipientLinkStatuses.Verified
                    && (item.UserId != link.UserId || item.ClientPortalAccountId != link.ClientPortalAccountId),
            ct);
        if (conflict)
            return "link-conflict";

        var oldLinks = await db.MaxRecipientLinks.Where(item =>
                item.Id != link.Id
                && item.Status == MaxRecipientLinkStatuses.Verified
                && ((link.UserId != null && item.UserId == link.UserId)
                    || (link.ClientPortalAccountId != null && item.ClientPortalAccountId == link.ClientPortalAccountId)
                    || item.MaxUserId == maxUserId))
            .ToListAsync(ct);
        foreach (var old in oldLinks)
        {
            old.Status = MaxRecipientLinkStatuses.Blocked;
            old.UpdatedAt = now;
        }

        link.MaxUserId = maxUserId;
        link.MaxChatId = chatId;
        link.Status = MaxRecipientLinkStatuses.Verified;
        link.VerifiedAt = now;
        link.LastSeenAt = now;
        link.UpdatedAt = now;
        return "link-verified";
    }

    private async Task<string> BlockLinksAsync(long? maxUserId, DateTime now, CancellationToken ct)
    {
        if (!maxUserId.HasValue) return "recipient-unknown";
        var links = await db.MaxRecipientLinks
            .Where(item => item.MaxUserId == maxUserId && item.Status == MaxRecipientLinkStatuses.Verified)
            .ToListAsync(ct);
        foreach (var link in links)
        {
            link.Status = MaxRecipientLinkStatuses.Blocked;
            link.OptedOutAt = now;
            link.UpdatedAt = now;
        }
        return links.Count == 0 ? "recipient-unknown" : "recipient-blocked";
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        var actualBytes = Encoding.UTF8.GetBytes(actual ?? string.Empty);
        return expectedBytes.Length == actualBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private static string? ReadString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static long? ReadInt64(JsonElement root, params string[] path)
    {
        var value = ReadString(root, path);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }
}
