using System.Globalization;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public static class PaymentAdvanceReviewService
{
    private const decimal AmountEpsilon = 0.009m;
    private const string AdvanceReviewRequiredEventKeyPrefix = "payment-advance-review-required:";
    private const string AdvanceReviewEscalationEventKeyPrefix = "payment-advance-review-escalation:";
    private static readonly Func<Payment, bool> UnallocatedAdvanceCandidateEvaluator =
        UnallocatedAdvanceCandidatePredicate().Compile();

    public static Expression<Func<Payment, bool>> UnallocatedAdvanceCandidatePredicate()
        => payment => payment.Type == PaymentType.Income
            && payment.DocumentKind == DocumentKind.Payment
            && payment.Status != PaymentStatus.Cancelled
            && ((payment.ClientId.HasValue && payment.ClientId.Value > 0 && !payment.LegalEntityId.HasValue)
                || (payment.LegalEntityId.HasValue && payment.LegalEntityId.Value > 0 && !payment.ClientId.HasValue))
            && (payment.PaidAmount > AmountEpsilon
                || ((payment.IsPaid || payment.Status == PaymentStatus.Completed) && payment.Amount > AmountEpsilon))
            && !payment.InvoiceId.HasValue
            && !payment.Acts.Any()
            && (payment.Account == null || payment.Account.Trim() == string.Empty)
            && (payment.LegacyAccountNumber == null || payment.LegacyAccountNumber.Trim() == string.Empty);

    public static bool IsUnallocatedAdvanceCandidate(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return UnallocatedAdvanceCandidateEvaluator(payment);
    }

    public static decimal GetAdvanceAmount(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var amount = payment.PaidAmount > AmountEpsilon
            ? payment.PaidAmount
            : payment.IsPaid || payment.Status == PaymentStatus.Completed
                ? payment.Amount
                : 0m;

        return NormalizeAmount(amount);
    }

    public static void ApplyAutomaticReviewState(Payment payment, DateTime utcNow, HttpContext? httpContext)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.AdvanceReviewStatus is PaymentAdvanceReviewStatus.Ignored or PaymentAdvanceReviewStatus.Refunded)
        {
            return;
        }

        if (IsUnallocatedAdvanceCandidate(payment))
        {
            if (payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance)
            {
                StampReviewIfNeeded(payment, utcNow, httpContext, "Принято как аванс");
                return;
            }

            payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
            payment.AdvanceReviewReason = null;
            payment.AdvanceReviewedAt = null;
            payment.AdvanceReviewedByUserId = null;
            payment.AdvanceReviewedByUserName = null;
            return;
        }

        if (payment.AdvanceReviewStatus is PaymentAdvanceReviewStatus.RequiresDecision or PaymentAdvanceReviewStatus.AcceptedAsAdvance)
        {
            payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Allocated;
            payment.AdvanceReviewReason = string.IsNullOrWhiteSpace(payment.AdvanceReviewReason)
                ? "Платеж связан со счетом, актом или закрывающим документом"
                : payment.AdvanceReviewReason;
            StampReviewIfNeeded(payment, utcNow, httpContext, payment.AdvanceReviewReason);
        }
    }

    public static async Task<(bool Success, string? Error)> AcceptAsAdvanceAsync(
        PaymentContext db,
        Payment payment,
        string? reason,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        PaymentDomainService.Normalize(payment);
        if (!IsUnallocatedAdvanceCandidate(payment))
        {
            await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id, ct);
            return (false, "Платеж уже связан со счетом, актом или закрывающим документом, либо не является входящим оплаченным платежом.");
        }

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? "Деньги приняты как нераспределенный аванс клиента";
        StampReviewIfNeeded(payment, utcNow, httpContext, payment.AdvanceReviewReason);
        await SyncBalanceAsync(db, payment, utcNow, httpContext, ct);
        return (true, null);
    }

    public static async Task<(bool Success, string? Error)> IgnoreAsync(
        PaymentContext db,
        Payment payment,
        string? reason,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Ignored;
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? "Исключено из контроля нераспределенных авансов";
        StampReviewIfNeeded(payment, utcNow, httpContext, payment.AdvanceReviewReason);
        await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id, ct);
        return (true, null);
    }

    public static async Task SyncBalanceAsync(
        PaymentContext db,
        Payment payment,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.Id <= 0)
        {
            return;
        }

        if (payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance
            && IsUnallocatedAdvanceCandidate(payment))
        {
            await ClientBalanceService.UpsertUnallocatedAdvanceAsync(
                db,
                payment,
                GetAdvanceAmount(payment),
                payment.AdvanceReviewReason,
                utcNow,
                httpContext,
                ct);
            return;
        }

        await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id, ct);
    }

    public static string BuildAdvanceReviewRequiredEventKey(int paymentId)
        => $"{AdvanceReviewRequiredEventKeyPrefix}{paymentId}";

    public static string BuildAdvanceReviewEscalationEventKey(int paymentId, int criticalAgeDays)
        => $"{AdvanceReviewEscalationEventKeyPrefix}{paymentId}:{criticalAgeDays}";

    public static int GetAdvanceReviewAgeDays(Payment payment, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var start = ResolveAdvanceReviewAgeStart(payment).Date;
        return Math.Max(0, (utcNow.Date - start).Days);
    }

    public static async Task<bool> EnsureReviewRequiredNotificationAsync(
        PaymentContext db,
        Payment payment,
        DateTime utcNow,
        INotificationService? notificationService = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.Id <= 0
            || payment.AdvanceReviewStatus != PaymentAdvanceReviewStatus.RequiresDecision
            || !IsUnallocatedAdvanceCandidate(payment))
        {
            return false;
        }

        if (notificationService is null)
        {
            return false;
        }

        var recipientUserId = await ResolveReviewRecipientUserIdAsync(db, payment, ct);
        if (!recipientUserId.HasValue)
        {
            return false;
        }

        var eventKey = BuildAdvanceReviewRequiredEventKey(payment.Id);
        var alreadyExists = await db.UserNotifications
            .AsNoTracking()
            .AnyAsync(item => item.RecipientUserId == recipientUserId.Value && item.EventKey == eventKey, ct);
        if (alreadyExists)
        {
            return false;
        }

        var recipientExists = await db.Users
            .AsNoTracking()
            .AnyAsync(item => item.Id == recipientUserId.Value && item.IsActive, ct);
        if (!recipientExists)
        {
            return false;
        }

        var disabledTypes = await db.UserSettings
            .AsNoTracking()
            .Where(item => item.UserId == recipientUserId.Value
                && item.Key == UserNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);
        if (UserNotificationPreferenceSettings.ParseDisabledTypes(disabledTypes)
            .Contains(UserNotificationTypes.PaymentAdvanceReviewRequired, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var counterpartyName = await ResolveCounterpartyNameAsync(db, payment, ct);
        var advanceAmount = GetAdvanceAmount(payment);
        var formattedAmount = advanceAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        var title = $"Платеж требует решения по авансу: {counterpartyName}";
        var body = $"Поступил платеж на {formattedAmount} ₽ без счета и акта. Проверьте его в разделе «Платежи» → «Контроль аванса».";

        return await notificationService.NotifyAsync(
            new NotificationEventEnvelope(
                recipientUserId.Value,
                UserNotificationTypes.PaymentAdvanceReviewRequired,
                eventKey,
                title,
                body,
                "payment",
                payment.Id,
                null,
                "rose",
                new
                {
                    paymentId = payment.Id,
                    paymentDate = payment.Date,
                    paymentType = payment.Type.ToString(),
                    clientId = payment.ClientId,
                    legalEntityId = payment.LegalEntityId,
                    clientCaseId = payment.ClientCaseId,
                    contractId = payment.ContractId,
                    advanceAmount,
                    advanceReviewStatus = payment.AdvanceReviewStatus.ToString(),
                }),
            ct);
    }

    public static async Task<int> EnsureReviewEscalationNotificationsAsync(
        PaymentContext db,
        Payment payment,
        DateTime utcNow,
        int criticalAgeDays,
        INotificationService? notificationService = null,
        CancellationToken ct = default,
        Func<NotificationEventEnvelope, CancellationToken, Task<bool>>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.Id <= 0
            || payment.AdvanceReviewStatus != PaymentAdvanceReviewStatus.RequiresDecision
            || !IsUnallocatedAdvanceCandidate(payment))
        {
            return 0;
        }

        if (notificationService is null)
        {
            return 0;
        }

        var normalizedCriticalAgeDays = Math.Max(1, criticalAgeDays);
        var ageDays = GetAdvanceReviewAgeDays(payment, utcNow);
        if (ageDays < normalizedCriticalAgeDays)
        {
            return 0;
        }

        var adminUserIds = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive
                && user.IsApproved
                && user.Role != null
                && user.Role.Name == "admin")
            .Select(user => user.Id)
            .ToListAsync(ct);
        if (adminUserIds.Count == 0)
        {
            return 0;
        }

        var eventKey = BuildAdvanceReviewEscalationEventKey(payment.Id, normalizedCriticalAgeDays);
        var existingRecipientIds = await db.UserNotifications
            .AsNoTracking()
            .Where(item => item.EventKey == eventKey && adminUserIds.Contains(item.RecipientUserId))
            .Select(item => item.RecipientUserId)
            .ToListAsync(ct);
        var existingRecipients = existingRecipientIds.ToHashSet();

        var counterpartyName = await ResolveCounterpartyNameAsync(db, payment, ct);
        var advanceAmount = GetAdvanceAmount(payment);
        var formattedAmount = advanceAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        var title = $"Просроченный аванс без решения: {counterpartyName}";
        var body = $"Платеж на {formattedAmount} ₽ ожидает решения по авансу уже {ageDays} дн. Проверьте его в разделе «Платежи» → «Контроль аванса».";

        var createdCount = 0;
        foreach (var adminUserId in adminUserIds)
        {
            if (existingRecipients.Contains(adminUserId))
            {
                continue;
            }

            var envelope = new NotificationEventEnvelope(
                    adminUserId,
                    UserNotificationTypes.PaymentAdvanceReviewEscalation,
                    eventKey,
                    title,
                    body,
                    "payment",
                    payment.Id,
                    null,
                    "rose",
                    new
                    {
                        paymentId = payment.Id,
                        paymentDate = payment.Date,
                        paidDate = payment.PaidDate,
                        lastPaymentDate = payment.LastPaymentDate,
                        clientId = payment.ClientId,
                        legalEntityId = payment.LegalEntityId,
                        clientCaseId = payment.ClientCaseId,
                        contractId = payment.ContractId,
                        advanceAmount,
                        ageDays,
                        criticalAgeDays = normalizedCriticalAgeDays,
                        advanceReviewStatus = payment.AdvanceReviewStatus.ToString(),
                    });
            var created = dispatch is null
                ? await notificationService.NotifyAsync(envelope, ct)
                : await dispatch(envelope, ct);

            if (created)
            {
                createdCount++;
            }
        }

        return createdCount;
    }

    public static void MarkAllocated(Payment payment, string? reason, DateTime utcNow, HttpContext? httpContext)
    {
        ArgumentNullException.ThrowIfNull(payment);

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Allocated;
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? "Аванс полностью распределен";
        payment.AdvanceReviewedAt = utcNow;
        payment.AdvanceReviewedByUserId = httpContext?.User.GetUserId() ?? payment.AdvanceReviewedByUserId;
        payment.AdvanceReviewedByUserName = httpContext?.User.GetDisplayName() ?? payment.AdvanceReviewedByUserName;
    }

    private static void StampReviewIfNeeded(Payment payment, DateTime utcNow, HttpContext? httpContext, string? reason)
    {
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? payment.AdvanceReviewReason;
        payment.AdvanceReviewedAt ??= utcNow;
        payment.AdvanceReviewedByUserId ??= httpContext?.User.GetUserId();
        payment.AdvanceReviewedByUserName ??= httpContext?.User.GetDisplayName();
    }

    private static string? NormalizeReason(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static DateTime ResolveAdvanceReviewAgeStart(Payment payment)
    {
        var date = payment.LastPaymentDate
            ?? payment.PaidDate
            ?? (payment.Date == default ? payment.CreatedAt : payment.Date);

        return date == default ? DateTime.UtcNow : date;
    }

    private static async Task<int?> ResolveReviewRecipientUserIdAsync(
        PaymentContext db,
        Payment payment,
        CancellationToken ct)
    {
        var clientResponsibleUserId = payment.Client?.ResponsibleUserId ?? 0;
        if (clientResponsibleUserId > 0)
        {
            return clientResponsibleUserId;
        }

        var legalResponsibleUserId = payment.LegalEntity?.ResponsibleUserId ?? 0;
        if (legalResponsibleUserId > 0)
        {
            return legalResponsibleUserId;
        }

        var caseResponsibleUserId = payment.ClientCase?.ResponsibleUserId ?? 0;
        if (caseResponsibleUserId > 0)
        {
            return caseResponsibleUserId;
        }

        if (payment.ClientId.HasValue)
        {
            var clientUserId = await db.Clients
                .AsNoTracking()
                .Where(item => item.Id == payment.ClientId.Value)
                .Select(item => item.ResponsibleUserId)
                .FirstOrDefaultAsync(ct);
            if (clientUserId is > 0)
            {
                return clientUserId;
            }
        }

        if (payment.LegalEntityId.HasValue)
        {
            var legalEntityUserId = await db.LegalEntities
                .AsNoTracking()
                .Where(item => item.Id == payment.LegalEntityId.Value)
                .Select(item => item.ResponsibleUserId)
                .FirstOrDefaultAsync(ct);
            if (legalEntityUserId is > 0)
            {
                return legalEntityUserId;
            }
        }

        if (payment.ClientCaseId.HasValue)
        {
            var caseUserId = await db.ClientCases
                .AsNoTracking()
                .Where(item => item.Id == payment.ClientCaseId.Value)
                .Select(item => item.ResponsibleUserId)
                .FirstOrDefaultAsync(ct);
            if (caseUserId is > 0)
            {
                return caseUserId;
            }
        }

        return null;
    }

    private static async Task<string> ResolveCounterpartyNameAsync(
        PaymentContext db,
        Payment payment,
        CancellationToken ct)
    {
        var name = payment.Client?.Name
            ?? (!string.IsNullOrWhiteSpace(payment.LegalEntity?.ShortName)
                ? payment.LegalEntity.ShortName
                : payment.LegalEntity?.FullName)
            ?? payment.CounterpartyName;

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        if (payment.ClientId.HasValue)
        {
            name = await db.Clients
                .AsNoTracking()
                .Where(item => item.Id == payment.ClientId.Value)
                .Select(item => item.Name)
                .FirstOrDefaultAsync(ct);
        }
        else if (payment.LegalEntityId.HasValue)
        {
            name = await db.LegalEntities
                .AsNoTracking()
                .Where(item => item.Id == payment.LegalEntityId.Value)
                .Select(item => string.IsNullOrWhiteSpace(item.ShortName) ? item.FullName : item.ShortName)
                .FirstOrDefaultAsync(ct);
        }

        return string.IsNullOrWhiteSpace(name) ? "контрагент не указан" : name.Trim();
    }
}
