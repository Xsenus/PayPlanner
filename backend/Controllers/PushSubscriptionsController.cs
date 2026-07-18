using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications/inbox/push")]
public sealed class PushSubscriptionsController : ControllerBase
{
    private readonly PaymentContext _context;
    private readonly IWebPushNotificationService _webPushNotificationService;

    public PushSubscriptionsController(
        PaymentContext context,
        IWebPushNotificationService webPushNotificationService)
    {
        _context = context;
        _webPushNotificationService = webPushNotificationService;
    }

    [HttpGet("public-key")]
    public ActionResult<WebPushPublicKeyDto> GetPublicKey()
    {
        var status = _webPushNotificationService.GetPublicKeyStatus();
        return Ok(new WebPushPublicKeyDto
        {
            Enabled = status.Enabled,
            Ready = status.Ready,
            PublicKey = status.PublicKey,
        });
    }

    [HttpGet("subscriptions")]
    public async Task<ActionResult<IReadOnlyList<UserPushSubscriptionDto>>> GetSubscriptions(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var subscriptions = await _context.UserPushSubscriptions
            .Where(item => item.UserId == userId.Value)
            .OrderByDescending(item => item.IsActive)
            .ThenByDescending(item => item.LastSeenAt ?? item.UpdatedAt)
            .ToListAsync(ct);

        if (RevokeInvalidStoredSubscriptions(subscriptions))
        {
            await _context.SaveChangesAsync(ct);
        }

        return Ok(subscriptions.Select(Map).ToList());
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<WebPushDiagnosticsDto>> GetDiagnostics(CancellationToken ct)
    {
        if (!await CanViewNotificationSettingsAsync(ct))
        {
            return Forbid();
        }

        await RevokeInvalidStoredSubscriptionsAsync(ct);

        var status = _webPushNotificationService.GetPublicKeyStatus();
        var activeSubscriptionsQuery = _context.UserPushSubscriptions
            .AsNoTracking()
            .Where(item => item.IsActive && item.RevokedAt == null);
        var subscriptionStatistics = await _context.UserPushSubscriptions
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Active = group.Count(item => item.IsActive && item.RevokedAt == null),
                Revoked = group.Count(item => !item.IsActive || item.RevokedAt != null),
                Failed = group.Count(item => item.LastErrorAt != null),
                UsersWithActive = group
                    .Where(item => item.IsActive && item.RevokedAt == null)
                    .Select(item => item.UserId)
                    .Distinct()
                    .Count(),
            })
            .SingleOrDefaultAsync(ct);

        var lastSuccessfulDeliveryAt = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.WebPush && item.Status == NotificationDeliveryStatus.Sent)
            .OrderByDescending(item => item.SentAt ?? item.CreatedAt)
            .Select(item => (DateTime?)(item.SentAt ?? item.CreatedAt))
            .FirstOrDefaultAsync(ct);

        var recentFailedDeliveries = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.WebPush && item.Status == NotificationDeliveryStatus.Failed)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, item.RecipientAddress, item.Error })
            .Take(50)
            .ToListAsync(ct);
        var activeEndpoints = await activeSubscriptionsQuery
            .Select(item => item.Endpoint)
            .ToListAsync(ct);
        var activeEndpointSet = activeEndpoints.ToHashSet(StringComparer.Ordinal);
        var lastFailedDelivery = recentFailedDeliveries.FirstOrDefault(item =>
            !WebPushSubscriptionHealth.IsInvalidSubscriptionError(item.Error)
            || activeEndpointSet.Contains(item.RecipientAddress));

        return Ok(new WebPushDiagnosticsDto
        {
            Enabled = status.Enabled,
            Ready = status.Ready,
            PublicKeyConfigured = status.PublicKeyConfigured,
            PrivateKeyConfigured = status.PrivateKeyConfigured,
            SubjectConfigured = status.SubjectConfigured,
            TotalSubscriptionsCount = subscriptionStatistics?.Total ?? 0,
            ActiveSubscriptionsCount = subscriptionStatistics?.Active ?? 0,
            RevokedSubscriptionsCount = subscriptionStatistics?.Revoked ?? 0,
            FailedSubscriptionsCount = subscriptionStatistics?.Failed ?? 0,
            UsersWithActiveSubscriptionsCount = subscriptionStatistics?.UsersWithActive ?? 0,
            LastSuccessfulDeliveryAt = lastSuccessfulDeliveryAt,
            LastFailedDeliveryAt = lastFailedDelivery?.CreatedAt,
            LastDeliveryError = lastFailedDelivery?.Error,
        });
    }

    [HttpPost("subscriptions")]
    public async Task<ActionResult<UserPushSubscriptionDto>> UpsertSubscription(
        [FromBody] UpsertPushSubscriptionRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var endpoint = NormalizeRequired(request?.Endpoint, 2048);
        var p256dh = NormalizeRequired(request?.Keys?.P256dh, 512);
        var auth = NormalizeRequired(request?.Keys?.Auth, 256);
        if (endpoint is null || p256dh is null || auth is null)
        {
            return BadRequest("endpoint, keys.p256dh and keys.auth are required.");
        }

        var now = DateTime.UtcNow;
        var subscription = await _context.UserPushSubscriptions
            .FirstOrDefaultAsync(item => item.Endpoint == endpoint, ct);

        if (subscription is null)
        {
            subscription = new UserPushSubscription
            {
                Endpoint = endpoint,
                CreatedAt = now,
            };
            _context.UserPushSubscriptions.Add(subscription);
        }

        subscription.UserId = userId.Value;
        subscription.P256dh = p256dh;
        subscription.Auth = auth;
        subscription.Browser = NormalizeOptional(request?.Browser, 120) ?? string.Empty;
        subscription.Platform = NormalizeOptional(request?.Platform, 120) ?? string.Empty;
        subscription.DeviceName = NormalizeOptional(request?.DeviceName, 160) ?? string.Empty;
        subscription.UserAgent = NormalizeOptional(request?.UserAgent, 512)
            ?? NormalizeOptional(Request.Headers.UserAgent.ToString(), 512)
            ?? string.Empty;
        subscription.IsActive = true;
        subscription.UpdatedAt = now;
        subscription.LastSeenAt = now;
        subscription.RevokedAt = null;
        subscription.LastErrorAt = null;
        subscription.LastError = null;

        await _context.SaveChangesAsync(ct);
        return Ok(Map(subscription));
    }

    [HttpDelete("subscriptions/{id:int}")]
    public async Task<IActionResult> RevokeSubscriptionById(int id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var subscription = await _context.UserPushSubscriptions
            .FirstOrDefaultAsync(item => item.Id == id && item.UserId == userId.Value, ct);
        if (subscription is null)
        {
            return NotFound();
        }

        Revoke(subscription);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("subscriptions/revoke")]
    public async Task<IActionResult> RevokeSubscriptionByEndpoint(
        [FromBody] RevokePushSubscriptionRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var endpoint = NormalizeRequired(request?.Endpoint, 2048);
        if (endpoint is null)
        {
            return BadRequest("endpoint is required.");
        }

        var subscription = await _context.UserPushSubscriptions
            .FirstOrDefaultAsync(item => item.Endpoint == endpoint && item.UserId == userId.Value, ct);
        if (subscription is null)
        {
            return NoContent();
        }

        Revoke(subscription);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Revoke(UserPushSubscription subscription)
    {
        var now = DateTime.UtcNow;
        subscription.IsActive = false;
        subscription.UpdatedAt = now;
        subscription.RevokedAt ??= now;
    }

    private async Task RevokeInvalidStoredSubscriptionsAsync(CancellationToken ct)
    {
        var subscriptions = await _context.UserPushSubscriptions
            .Where(item => item.IsActive && item.RevokedAt == null && item.LastErrorAt != null)
            .ToListAsync(ct);

        var changed = RevokeInvalidStoredSubscriptions(subscriptions);

        var failedDeliveries = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.WebPush
                && item.Status == NotificationDeliveryStatus.Failed
                && item.RecipientAddress != string.Empty)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.RecipientAddress, item.Error })
            .Take(500)
            .ToListAsync(ct);
        var invalidEndpointErrors = failedDeliveries
            .Where(item => WebPushSubscriptionHealth.IsInvalidSubscriptionError(item.Error))
            .GroupBy(item => item.RecipientAddress, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.First().Error, StringComparer.Ordinal);
        if (invalidEndpointErrors.Count > 0)
        {
            var invalidEndpoints = invalidEndpointErrors.Keys.ToList();
            var subscriptionsWithFailedDeliveries = await _context.UserPushSubscriptions
                .Where(item => item.IsActive && item.RevokedAt == null && invalidEndpoints.Contains(item.Endpoint))
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var subscription in subscriptionsWithFailedDeliveries)
            {
                if (!invalidEndpointErrors.TryGetValue(subscription.Endpoint, out var error)
                    || !WebPushSubscriptionHealth.TryRevokeInvalidSubscription(subscription, now, error))
                {
                    continue;
                }

                subscription.LastErrorAt ??= now;
                subscription.LastError ??= error;
                changed = true;
            }
        }

        if (changed)
        {
            await _context.SaveChangesAsync(ct);
        }
    }

    private static bool RevokeInvalidStoredSubscriptions(IReadOnlyCollection<UserPushSubscription> subscriptions)
    {
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var subscription in subscriptions)
        {
            changed |= WebPushSubscriptionHealth.TryRevokeInvalidSubscription(subscription, now);
        }

        return changed;
    }

    private static UserPushSubscriptionDto Map(UserPushSubscription item)
        => new()
        {
            Id = item.Id,
            Endpoint = item.Endpoint,
            Browser = item.Browser,
            Platform = item.Platform,
            DeviceName = item.DeviceName,
            IsActive = item.IsActive,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            LastSeenAt = item.LastSeenAt,
            RevokedAt = item.RevokedAt,
            LastErrorAt = item.LastErrorAt,
            LastError = item.LastError,
        };

    private static string? NormalizeRequired(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
        {
            return null;
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private async Task<bool> CanViewNotificationSettingsAsync(CancellationToken ct)
        => await _context.HasUserSectionPermissionAsync(
            User,
            RolePermissionCatalog.Notifications,
            static permission => permission.CanView,
            ct);
}
