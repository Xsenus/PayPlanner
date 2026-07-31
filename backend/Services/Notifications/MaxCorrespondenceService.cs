using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record MaxCorrespondenceItem(
    Guid PublicId,
    int PortalAccountId,
    string PortalDisplayName,
    int? ClientCaseId,
    string? CaseNumber,
    string Text,
    string Status,
    string ExternalMessageId,
    DateTime OccurredAt,
    DateTime ExpiresAt);

public interface IMaxCorrespondenceService
{
    Task<string> CaptureAsync(
        MaxInboundEvent inbound,
        long? maxUserId,
        string? externalMessageId,
        string? text,
        DateTime occurredAt,
        int retentionDays,
        CancellationToken ct);
    Task<IReadOnlyList<MaxCorrespondenceItem>> GetAsync(string? status, int take, CancellationToken ct);
    Task<bool> RouteAsync(Guid publicId, int clientCaseId, int routedByUserId, CancellationToken ct);
}

public sealed class MaxCorrespondenceService(
    PaymentContext db,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider) : IMaxCorrespondenceService
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("PayPlanner.Notifications.Max.Correspondence.v1");

    public async Task<string> CaptureAsync(
        MaxInboundEvent inbound,
        long? maxUserId,
        string? externalMessageId,
        string? text,
        DateTime occurredAt,
        int retentionDays,
        CancellationToken ct)
    {
        var normalizedText = text?.Trim();
        if (!maxUserId.HasValue || string.IsNullOrWhiteSpace(externalMessageId) || string.IsNullOrWhiteSpace(normalizedText))
            return "correspondence-invalid";
        if (normalizedText.Length > 4000)
            return "correspondence-too-large";

        var link = await db.MaxRecipientLinks
            .AsNoTracking()
            .Where(item => item.MaxUserId == maxUserId
                && item.Status == MaxRecipientLinkStatuses.Verified
                && item.ClientPortalAccountId != null)
            .OrderByDescending(item => item.VerifiedAt)
            .FirstOrDefaultAsync(ct);
        if (link?.ClientPortalAccountId is not int portalAccountId)
            return "correspondence-recipient-unlinked";
        var normalizedExternalId = externalMessageId.Trim()[..Math.Min(externalMessageId.Trim().Length, 256)];
        if (await db.MaxInboundCorrespondenceMessages.AsNoTracking().AnyAsync(
            item => item.RecipientLinkId == link.Id && item.ExternalMessageId == normalizedExternalId, ct))
            return "correspondence-duplicate";

        var caseIds = await db.ClientPortalCaseGrants.AsNoTracking()
            .Where(item => item.ClientPortalAccountId == portalAccountId
                && item.RevokedAt == null
                && item.CanViewMessages)
            .OrderBy(item => item.ClientCaseId)
            .Select(item => item.ClientCaseId)
            .Take(2)
            .ToListAsync(ct);
        if (caseIds.Count == 0)
            return "correspondence-access-denied";

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.MaxInboundCorrespondenceMessages.Add(new MaxInboundCorrespondenceMessage
        {
            InboundEvent = inbound,
            RecipientLinkId = link.Id,
            ClientPortalAccountId = portalAccountId,
            ClientCaseId = caseIds.Count == 1 ? caseIds[0] : null,
            ExternalMessageId = normalizedExternalId,
            ProtectedText = _protector.Protect(normalizedText),
            Status = caseIds.Count == 1 ? MaxCorrespondenceStatuses.Routed : MaxCorrespondenceStatuses.PendingRouting,
            OccurredAt = occurredAt,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(retentionDays, 1, 90)),
            RoutedAt = caseIds.Count == 1 ? now : null
        });
        return caseIds.Count == 1 ? "correspondence-routed" : "correspondence-pending-manual-routing";
    }

    public async Task<IReadOnlyList<MaxCorrespondenceItem>> GetAsync(string? status, int take, CancellationToken ct)
    {
        var normalizedStatus = status?.Trim().ToLowerInvariant();
        var query = db.MaxInboundCorrespondenceMessages.AsNoTracking()
            .Where(item => item.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime);
        if (normalizedStatus is MaxCorrespondenceStatuses.Routed or MaxCorrespondenceStatuses.PendingRouting)
            query = query.Where(item => item.Status == normalizedStatus);
        var rows = await query.OrderByDescending(item => item.OccurredAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(item => new
            {
                item.PublicId,
                item.ClientPortalAccountId,
                item.ClientPortalAccount.DisplayName,
                item.ClientCaseId,
                CaseNumber = item.ClientCase == null ? null : item.ClientCase.CaseNumber,
                item.ProtectedText,
                item.Status,
                item.ExternalMessageId,
                item.OccurredAt,
                item.ExpiresAt
            })
            .ToListAsync(ct);
        return rows.Select(item => new MaxCorrespondenceItem(
            item.PublicId,
            item.ClientPortalAccountId,
            item.DisplayName,
            item.ClientCaseId,
            item.CaseNumber,
            _protector.Unprotect(item.ProtectedText),
            item.Status,
            item.ExternalMessageId,
            item.OccurredAt,
            item.ExpiresAt)).ToList();
    }

    public async Task<bool> RouteAsync(Guid publicId, int clientCaseId, int routedByUserId, CancellationToken ct)
    {
        var message = await db.MaxInboundCorrespondenceMessages
            .FirstOrDefaultAsync(item => item.PublicId == publicId && item.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime, ct);
        if (message is null) return false;
        var allowed = await db.ClientPortalCaseGrants.AsNoTracking().AnyAsync(item =>
            item.ClientPortalAccountId == message.ClientPortalAccountId
            && item.ClientCaseId == clientCaseId
            && item.RevokedAt == null
            && item.CanViewMessages, ct);
        if (!allowed) throw new InvalidOperationException("У аккаунта нет доступа к переписке выбранного дела.");
        message.ClientCaseId = clientCaseId;
        message.Status = MaxCorrespondenceStatuses.Routed;
        message.RoutedAt = timeProvider.GetUtcNow().UtcDateTime;
        message.RoutedByUserId = routedByUserId;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
