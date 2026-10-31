using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private const decimal AmountTolerance = 0.009m;
    private static readonly Regex LegacyInstallmentDescriptionRegex = new(
        @"Рассрочка\s+\d+\s*/\s*\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private const string LegacyPartialPaymentNoteText = "Частичная оплата";

    private static readonly Regex RemainderDateHintRegex = new(
        @"(?:Остаток[^\r\n]{0,120}(?:->|→|перенесен(?:о)?\s+на)|перенесен(?:о)?\s+на)\s*(\d{2}\.\d{2}\.\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LegacyPartialRemainderNoteRegex = new(
        @"Частичная\s+оплата:\s*(?<paid>[\d\s.,]+)\s*/\s*(?<original>[\d\s.,]+)[^\r\n]*?Остаток\s*(?<remainder>[\d\s.,]+)(?:\s*(?:->|→|перенес[^\d\r\n]{0,60}))?\s*(?<date>\d{2}\.\d{2}\.\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;
    private readonly INotificationService? _notificationService;
    private bool _skipRemainderTransferFormRequirements;
    public PaymentsController(
        PaymentContext db,
        IFormRequirementsService formRequirements,
        INotificationService? notificationService = null)
    {
        _db = db;
        _formRequirements = formRequirements;
        _notificationService = notificationService;
    }

    private Task<DataVisibilityAccess?> ResolvePaymentsVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);

    private ActionResult AccessDenied()
        => User.GetUserId().HasValue ? Forbid() : Unauthorized();

    private Task<TimeZoneInfo> ResolvePaymentTimeZoneAsync(CancellationToken ct)
        => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);

    private sealed record InvoiceNotificationSnapshot(
        int PaymentId,
        PaymentStatus Status,
        string WorkflowStatus,
        decimal PaidAmount);

    private async Task<bool> CanAccessPaymentAsync(int id, DataVisibilityAccess visibility, CancellationToken ct)
        => await _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .ApplyUnambiguousInvoiceLinks()
            .AnyAsync(payment => payment.Id == id, ct);

    private async Task<IActionResult?> ValidateInvoiceLinkAccessAsync(Payment payment, CancellationToken ct)
    {
        if (!payment.InvoiceId.HasValue) return null;
        var visibility = await ResolvePaymentsVisibilityAsync(ct)
            ?? await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility?.SeesAll == true
            && await _db.Invoices.AnyAsync(invoice => invoice.Id == payment.InvoiceId && !invoice.Payments.Any(), ct))
            return null;
        if (visibility is null || !await _db.Payments.AsNoTracking().ApplyPaymentVisibility(visibility)
            .ApplyUnambiguousInvoiceLinks().AnyAsync(document => document.InvoiceId == payment.InvoiceId
                && document.DocumentKind == DocumentKind.Invoice, ct))
            return NotFound();
        return null;
    }

    private async Task<int[]> ResolvePaymentNotificationCandidatesAsync(
        IEnumerable<int> candidateUserIds,
        CancellationToken ct)
    {
        var userIds = candidateUserIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
        {
            return [];
        }

        return await _db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && (user.Role != null && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Payments
                        && permission.CanView))))
            .Select(user => user.Id)
            .ToArrayAsync(ct);
    }

    private async Task<int[]> ResolveInvoiceNotificationCandidatesAsync(
        IEnumerable<int> candidateUserIds,
        CancellationToken ct)
    {
        var userIds = candidateUserIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
        {
            return [];
        }

        return await _db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Accounts
                        && permission.CanView)))
            .Select(user => user.Id)
            .ToArrayAsync(ct);
    }

    private async Task<IActionResult?> RequirePaymentsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, isAllowed, ct)
            ? null
            : Forbid();

    private Task<IActionResult?> RequirePaymentsEditAsync(CancellationToken ct)
        => RequirePaymentsPermissionAsync(permission => permission.CanEdit, ct);

    private sealed class PaymentWriteScope(BalanceApplicationLease lease, PaymentContext db) : IDisposable
    {
        private readonly PaymentCreditInvariantScope credit = new(db);
        private readonly HashSet<int> lockedRelatedPayments = [];
        public Func<Task>? AfterCommit { get; set; }
        public bool CancellationProtected { get; set; }
        public bool ClosedReceiptMetadataOnly { get; set; }
        public async Task LockPaymentAsync(int id, CancellationToken ct)
        {
            if (!lockedRelatedPayments.Add(id)) return;
            await lease.LockPaymentAsync(id, ct);
            if (await PaymentCreditInvariantScope.IsCancellationProtectedAsync(db, id, ct)) throw new CancellationProtectedPaymentException();
        }
        public Task<bool> KeepsAppliedCreditAsync(CancellationToken ct) => credit.KeepsAppliedCreditAsync(ct);
        public void Dispose() { credit.Dispose(); lockedRelatedPayments.Clear(); }
    }

    private sealed class CancellationProtectedPaymentException : Exception;

    private async Task<IActionResult> RunPaymentWriteAsync(int id,
        Func<PaymentWriteScope, Task<IActionResult>> write, CancellationToken ct, bool allowClosedMetadata = false, bool deleting = false)
    {
        var actorId = User.GetUserId();
        if (User?.Identity?.IsAuthenticated != true || actorId is not > 0) return Unauthorized();
        BalanceApplicationLease? lease = null;
        PaymentWriteScope? scope = null;
        IActionResult result;
        try
        {
            // Participate before reading paid amounts, notes, permissions or balance
            // entries. The managed Apply path owns the same payment row lock.
            lease = await BalanceApplicationLease.BeginAsync(_db, actorId.Value, ct);
            scope = new(lease, _db);
            if (_db.Database.IsNpgsql()) await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return Unauthorized();
            var forbidden = deleting ? await RequirePaymentsPermissionAsync(permission => permission.CanDelete, ct) : await RequirePaymentsEditAsync(ct);
            if (forbidden is not null) return forbidden;
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null) return AccessDenied();
            if (!await CanAccessPaymentAsync(id, visibility, ct)) return NotFound();
            await lease.LockPaymentAsync(id, ct);
            scope.CancellationProtected = await PaymentCreditInvariantScope.IsCancellationProtectedAsync(_db, id, ct);
            if (scope.CancellationProtected && !allowClosedMetadata)
                return BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
            var balanceBefore = scope.CancellationProtected ? null : await PaymentBalanceHistoryRules.CaptureOwnerBalanceAsync(_db, id, ct);
            result = await write(scope);
            if (result is OkObjectResult or NoContentResult)
            {
                if (!scope.ClosedReceiptMetadataOnly && !await scope.KeepsAppliedCreditAsync(ct))
                    return BadRequest(new { message = PaymentCreditInvariantScope.RejectionMessage });
                if (!await PaymentBalanceHistoryRules.KeepsOwnerBalanceAsync(_db, balanceBefore, ct))
                    return BadRequest(new { message = "Изменение создало бы отрицательный остаток баланса. Требуется сверка оплаты." });
                await lease.CommitAsync(ct);
            }
        }
        catch (CancellationProtectedPaymentException)
        {
            return BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
        }
        catch (BalanceSourceHistoryException error)
        {
            return BadRequest(new { message = error.Message });
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Платёж изменился или сейчас занят. Обновите карточку и повторите действие." });
        }
        finally
        {
            scope?.Dispose();
            if (lease is not null) await lease.DisposeAsync();
        }
        // Notifications may use another context. Never publish an uncommitted change
        // and never translate a post-commit failure into a retryable write conflict.
        if (result is OkObjectResult && scope?.AfterCommit is { } notify) await notify();
        return result;
    }

    private Task<IActionResult> RunRemainderWriteAsync(int id,
        Func<PaymentWriteScope, Task<IActionResult>> write, CancellationToken ct)
        => RunPaymentWriteAsync(id, async scope =>
        {
            if (await RequirePaymentsEditAsync(ct) is { } forbidden) return forbidden;
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null) return AccessDenied();
            if (!await CanAccessPaymentAsync(id, visibility, ct)) return NotFound();
            return await write(scope);
        }, ct);

    private async Task<IActionResult?> LockRemainderPairAsync(int id, PartialRemainderTransferPair pair,
        PaymentWriteScope scope, CancellationToken ct)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return AccessDenied();
        if (!await CanAccessPaymentAsync(pair.Source.Id, visibility, ct)
            || !await CanAccessPaymentAsync(pair.Remainder.Id, visibility, ct)) return NotFound();
        // The request root is already locked. NOWAIT avoids waiting on a caller
        // that entered through the other member of this same pair.
        if (pair.Source.Id != id) await scope.LockPaymentAsync(pair.Source.Id, ct);
        if (pair.Remainder.Id != id) await scope.LockPaymentAsync(pair.Remainder.Id, ct);
        return null;
    }

    private async Task NotifyPaymentAssignedAsync(Payment payment, CancellationToken ct)
    {
        if (_notificationService is null)
        {
            return;
        }

        await _db.Entry(payment).Collection(item => item.Responsibles).LoadAsync(ct);
        var recipients = await ResolvePaymentNotificationCandidatesAsync(
            payment.Responsibles.Select(item => item.UserId),
            ct);
        if (recipients.Length == 0)
        {
            return;
        }

        await _notificationService.NotifyPaymentAssignedAsync(
            payment,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipients,
            ct);
    }

    private async Task NotifyPaymentChangedAsync(
        Payment payment,
        IReadOnlyList<string> changeLabels,
        CancellationToken ct)
    {
        if (_notificationService is null || changeLabels.Count == 0)
        {
            return;
        }

        await _db.Entry(payment).Collection(item => item.Responsibles).LoadAsync(ct);
        var recipients = await ResolvePaymentNotificationCandidatesAsync(
            payment.Responsibles.Select(item => item.UserId),
            ct);
        if (recipients.Length == 0)
        {
            return;
        }

        await _notificationService.NotifyPaymentChangedAsync(
            payment,
            changeLabels,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipients,
            ct);
    }

    private async Task<InvoiceNotificationSnapshot?> CaptureInvoiceNotificationSnapshotAsync(Payment payment, CancellationToken ct)
    {
        var invoicePayment = await ResolveInvoiceNotificationPaymentAsync(payment, tracking: false, ct);
        if (invoicePayment is null)
        {
            return null;
        }

        return new InvoiceNotificationSnapshot(
            invoicePayment.Id,
            invoicePayment.Status,
            InvoiceWorkflowStatus.Normalize(invoicePayment.InvoiceWorkflowStatus) ?? invoicePayment.InvoiceWorkflowStatus,
            invoicePayment.PaidAmount);
    }

    private async Task NotifyInvoiceStateChangedAsync(
        Payment payment,
        InvoiceNotificationSnapshot? before,
        CancellationToken ct)
    {
        if (_notificationService is null || before is null)
        {
            return;
        }

        var invoicePayment = await ResolveInvoiceNotificationPaymentAsync(payment, tracking: true, ct);
        if (invoicePayment is null || invoicePayment.Id != before.PaymentId)
        {
            return;
        }

        var changeLabels = BuildInvoicePaymentStateChangeLabels(before, invoicePayment);
        if (changeLabels.Count == 0)
        {
            return;
        }

        await _db.Entry(invoicePayment).Collection(item => item.Responsibles).LoadAsync(ct);
        await _db.Entry(invoicePayment).Reference(item => item.Invoice).LoadAsync(ct);
        var recipients = await ResolveInvoiceNotificationCandidatesAsync(
            invoicePayment.Responsibles.Select(item => item.UserId),
            ct);
        if (recipients.Length == 0)
        {
            return;
        }

        await _notificationService.NotifyInvoiceChangedAsync(
            invoicePayment,
            changeLabels,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipients,
            ct);
    }

    private async Task<Payment?> ResolveInvoiceNotificationPaymentAsync(
        Payment payment,
        bool tracking,
        CancellationToken ct)
    {
        if (payment.DocumentKind == DocumentKind.Invoice)
        {
            if (tracking)
            {
                return payment;
            }

            return await _db.Payments
                .AsNoTracking()
                .Include(item => item.Invoice)
                .Include(item => item.Responsibles)
                .FirstOrDefaultAsync(item => item.Id == payment.Id, ct);
        }

        var invoiceId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (!invoiceId.HasValue || invoiceId.Value <= 0)
        {
            return null;
        }

        var query = _db.Payments
            .Include(item => item.Invoice)
            .Include(item => item.Responsibles)
            .Where(item => item.InvoiceId == invoiceId.Value && item.DocumentKind == DocumentKind.Invoice);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static IReadOnlyList<string> BuildInvoicePaymentStateChangeLabels(
        InvoiceNotificationSnapshot before,
        Payment invoicePayment)
    {
        var labels = new List<string>();
        var currentWorkflowStatus = InvoiceWorkflowStatus.Normalize(invoicePayment.InvoiceWorkflowStatus) ?? invoicePayment.InvoiceWorkflowStatus;
        if (!string.Equals(before.WorkflowStatus, currentWorkflowStatus, StringComparison.Ordinal))
        {
            labels.Add($"Статус счета: {FormatInvoiceWorkflowStatus(before.WorkflowStatus)} -> {FormatInvoiceWorkflowStatus(currentWorkflowStatus)}");
        }

        if (before.Status != invoicePayment.Status)
        {
            labels.Add($"Статус оплаты: {before.Status.ToDisplayString()} -> {invoicePayment.Status.ToDisplayString()}");
        }

        if (Math.Abs(before.PaidAmount - invoicePayment.PaidAmount) > AmountTolerance)
        {
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            labels.Add($"Оплачено: {before.PaidAmount.ToString("N2", culture)} -> {invoicePayment.PaidAmount.ToString("N2", culture)}");
        }

        return labels;
    }

    private static string FormatInvoiceWorkflowStatus(string? status)
        => (InvoiceWorkflowStatus.Normalize(status) ?? status)?.Trim() switch
        {
            InvoiceWorkflowStatus.Draft => "Черновик",
            InvoiceWorkflowStatus.NotSigned => "Не подписан",
            InvoiceWorkflowStatus.RequiresClientSignature => "Требует подписи клиента",
            InvoiceWorkflowStatus.OnApproval => "На согласовании",
            InvoiceWorkflowStatus.Approved => "Согласован",
            InvoiceWorkflowStatus.ApprovalRejected => "Согласование отклонено",
            InvoiceWorkflowStatus.NeedsCorrection => "Требует корректировки",
            InvoiceWorkflowStatus.OnSigning => "Отправлен на подпись",
            InvoiceWorkflowStatus.SignedByClient => "Подписан клиентом",
            InvoiceWorkflowStatus.Paid => "Оплачен",
            InvoiceWorkflowStatus.PartiallyPaid => "Частично оплачен",
            InvoiceWorkflowStatus.Overdue => "Просрочен",
            InvoiceWorkflowStatus.Cancelled => "Отменен",
            _ => "не указан"
        };

    private static IReadOnlyList<string> BuildPaymentChangeLabels(
        PaymentStatus originalStatus,
        PaymentStatus currentStatus,
        DateTime originalDate,
        DateTime currentDate,
        decimal originalAmount,
        decimal currentAmount,
        int? originalPaymentSourceId,
        int? currentPaymentSourceId)
    {
        var labels = new List<string>();
        if (originalStatus != currentStatus)
        {
            labels.Add($"Статус: {originalStatus.ToDisplayString()} -> {currentStatus.ToDisplayString()}");
        }

        if (originalDate.Date != currentDate.Date)
        {
            labels.Add($"Дата: {originalDate:dd.MM.yyyy} -> {currentDate:dd.MM.yyyy}");
        }

        if (Math.Abs(originalAmount - currentAmount) > AmountTolerance)
        {
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            labels.Add($"Сумма: {originalAmount.ToString("N2", culture)} -> {currentAmount.ToString("N2", culture)}");
        }

        if (originalPaymentSourceId != currentPaymentSourceId)
        {
            labels.Add("Источник платежа изменен");
        }

        return labels;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] string? clientRole = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }

        var q = _db.Payments
            .AsQueryable()
            .ApplyPaymentVisibility(visibility)
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents)
            .ApplyPaymentFilters(from, to, clientId, caseId, null, clientRole, legalEntityId)
            .ApplyPaymentSort(sortBy, sortDir);

        var list = await ProjectPaymentsForRead(q)
            .ToListAsync(ct);
        PaymentDisplayNameService.FillCounterpartyNames(list);
        await AttachContractorRecipientsAsync(list, ct);
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }

        var item = await ProjectPaymentsForRead(_db.Payments
                .ApplyPaymentVisibility(visibility)
                .Where(p => p.Id == id))
            .FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return NotFound();
        }

        PaymentDisplayNameService.FillCounterpartyNames([item]);
        await AttachContractorRecipientsAsync(new[] { item }, ct);
        return Ok(item);
    }

    private TimeProvider AttemptTime => HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

    [HttpPost("create-attempts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RegisterCreateAttempt([FromBody] JsonElement body, CancellationToken ct)
    {
        if (await RequireAttemptAccessAsync(ct) is { } forbidden) return forbidden;
        if (!RegisterPaymentCreateAttemptRequest.TryRead(body, out var request) || request is null)
            return BadRequest(new { message = "Проверьте назначение попытки и выбранную компанию." });
        return await WithAttemptLeaseAsync(async lease =>
        {
            var attempt = new PaymentCreateAttempt
            {
                Id = Guid.NewGuid(),
                ActorUserId = User.GetUserId()!.Value,
                Operation = request.Operation,
                SellerCompanyId = request.SellerCompanyId,
                SellerBranchId = request.SellerBranchId,
                PrimaryPaymentId = request.PrimaryPaymentId,
                CreatedAtUtc = AttemptTime.GetUtcNow(),
            };
            attempt.ExpiresAtUtc = attempt.CreatedAtUtc.AddHours(24);
            if (await CheckAttemptScopeAsync(attempt, lease, invalidSelectionIsBadRequest: true, ct) is { } denied) return denied;
            _db.PaymentCreateAttempts.Add(attempt);
            await _db.SaveChangesAsync(ct);
            await lease.CommitAsync(ct);
            return Created($"/api/payments/create-attempts/{attempt.Id:D}", AttemptState(attempt));
        }, ct);
    }

    [HttpGet("create-attempts/{attemptId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetCreateAttempt(Guid attemptId, CancellationToken ct)
    {
        if (await RequireAttemptAccessAsync(ct) is { } forbidden) return forbidden;
        var actor = User.GetUserId()!.Value;
        var attempt = await _db.PaymentCreateAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == attemptId && a.ActorUserId == actor, ct);
        if (attempt is null || attempt.State == PaymentCreateAttempt.Draft && attempt.ExpiresAtUtc <= AttemptTime.GetUtcNow())
            return AttemptGone();
        if (await CheckAttemptScopeAsync(attempt, null, false, ct) is { } denied) return denied;
        // A Draft observation cannot prove that a queued commit will not run. Only Cancel locks/tombstones.
        return Ok(AttemptState(attempt));
    }

    [HttpPost("create-attempts/{attemptId:guid}/cancel")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CancelCreateAttempt(Guid attemptId, CancellationToken ct)
    {
        if (await RequireAttemptAccessAsync(ct) is { } forbidden) return forbidden;
        return await WithAttemptLeaseAsync(async lease =>
        {
            var attempt = await lease.LockAttemptAsync(attemptId, User.GetUserId()!.Value, ct);
            if (attempt is null) return AttemptGone();
            if (attempt.State == PaymentCreateAttempt.Committed)
            {
                // Canonical ID disclosure still requires every current relation/payment permission.
                if (await CheckAttemptScopeAsync(attempt, lease, false, ct) is { } denied) return denied;
                return Ok(new PaymentCreateAttemptCancelOutcomeDto(attempt.Id,
                    PaymentCreateAttempt.Committed, attempt.CanonicalPaymentId));
            }
            if (attempt.State == PaymentCreateAttempt.Draft)
            {
                // Even expired Draft can be authoritatively cancelled; never delete/recreate its UUID.
                // Only owned operational metadata changes; no old seller/primary IDs are disclosed.
                attempt.State = PaymentCreateAttempt.Cancelled;
                attempt.CancelledAtUtc = AttemptTime.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                await lease.CommitAsync(ct);
            }
            else if (attempt.State != PaymentCreateAttempt.Cancelled)
                throw new InvalidOperationException("Unknown payment attempt state.");
            return Ok(new PaymentCreateAttemptCancelOutcomeDto(attempt.Id,
                PaymentCreateAttempt.Cancelled, null));
        }, ct);
    }

    [HttpPost("create-attempts/{attemptId:guid}/commit")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CommitCreateAttempt(Guid attemptId, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (await RequireAttemptAccessAsync(ct) is { } forbidden) return forbidden;
        var options = HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        if (!PaymentCreateCommand.TryRead(body, options, out var model, out var identity, out var error)
            || model is null || identity is null)
            return BadRequest(new { message = error ?? "Некорректная команда платежа." });
        return await WithAttemptLeaseAsync(async lease =>
        {
            var attempt = await lease.LockAttemptAsync(attemptId, User.GetUserId()!.Value, ct);
            if (attempt is null || attempt.State == PaymentCreateAttempt.Cancelled
                || attempt.State == PaymentCreateAttempt.Draft && attempt.ExpiresAtUtc <= AttemptTime.GetUtcNow())
                return AttemptGone();
            if (attempt.IdentityVersion != PaymentCreateCommand.Version
                || attempt.SellerCompanyId != model.SellerCompanyId || attempt.SellerBranchId != model.SellerBranchId)
                return AttemptConflict();
            if (await CheckAttemptScopeAsync(attempt, lease, false, ct) is { } denied) return denied;
            if (attempt.State == PaymentCreateAttempt.Committed)
            {
                if (!string.Equals(attempt.PayloadIdentity, identity, StringComparison.Ordinal)) return AttemptConflict();
                // Existing projection/ACL; never update the canonical payment from replay input or notify twice.
                return await GetById(attempt.CanonicalPaymentId!.Value, ct);
            }
            if (attempt.State != PaymentCreateAttempt.Draft) throw new InvalidOperationException("Unknown payment attempt state.");
            if (PaymentCreateInputRules.ValidateCreationState(model) is { } inputStateError)
                return BadRequest(new { message = inputStateError });
            using var credit = new PaymentCreditInvariantScope(_db);
            var scope = new VersionedPaymentWriteBoundary.Scope(lease.LockPrimaryAsync);
            var result = await CreatePaymentCoreAsync(model, scope, ct);
            if (result is not CreatedResult) return result;
            if (!await credit.KeepsAppliedCreditAsync(ct))
                return BadRequest(new { message = PaymentCreditInvariantScope.RejectionMessage });
            if (attempt.ExpiresAtUtc <= AttemptTime.GetUtcNow()) throw new PaymentCreateAttemptExpiredException();
            attempt.State = PaymentCreateAttempt.Committed;
            attempt.PayloadIdentity = identity;
            attempt.CanonicalPaymentId = model.Id;
            attempt.CommittedAtUtc = AttemptTime.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            await lease.CommitAsync(ct);
            await lease.DisposeAsync();
            if (_db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("Payment notifications require a disposed financial transaction.");
            if (scope.AfterCommit is not null) await scope.AfterCommit();
            return result;
        }, ct);
    }

    private async Task<IActionResult?> RequireAttemptAccessAsync(CancellationToken ct)
    {
        var actor = User.GetUserId();
        if (User?.Identity?.IsAuthenticated != true || actor is not > 0) return Unauthorized();
        // New ownership metadata must never use legacy empty-DB or stale-claim permission fallbacks.
        if (!await _db.Users.AsNoTracking().AnyAsync(u => u.Id == actor.Value && u.IsActive && u.IsApproved, ct)) return Forbid();
        return await RequirePaymentsPermissionAsync(p => p.CanCreate, ct);
    }

    private async Task<IActionResult?> CheckAttemptScopeAsync(PaymentCreateAttempt attempt,
        PaymentCreateAttemptLease? lease, bool invalidSelectionIsBadRequest, CancellationToken ct)
    {
        var seller = new DocumentSellerService(_db);
        if (await seller.ValidateSelectionAsync(attempt.SellerCompanyId, attempt.SellerBranchId, ct) is { } selectionError)
            return invalidSelectionIsBadRequest ? BadRequest(new { message = selectionError }) : Forbid();
        if (lease is not null)
        {
            await lease.LockSellerSelectionAsync(attempt.SellerCompanyId, attempt.SellerBranchId, ct);
            if (await seller.ValidateSelectionAsync(attempt.SellerCompanyId, attempt.SellerBranchId, ct) is { } changedSelection)
                return invalidSelectionIsBadRequest ? BadRequest(new { message = changedSelection }) : Forbid();
        }
        if (attempt.PrimaryPaymentId.HasValue || attempt.State == PaymentCreateAttempt.Committed)
        {
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null) return AccessDenied();
            if (attempt.PrimaryPaymentId is { } primary)
            {
                if (!await CanAccessPaymentAsync(primary, visibility, ct)) return NotFound();
                if (lease is not null)
                {
                    await lease.LockPrimaryAsync(primary, ct);
                    if (!await CanAccessPaymentAsync(primary, visibility, ct)) return NotFound();
                }
            }
            if (attempt.State == PaymentCreateAttempt.Committed
                && (attempt.CanonicalPaymentId is not > 0
                    || !await CanAccessPaymentAsync(attempt.CanonicalPaymentId.Value, visibility, ct))) return NotFound();
        }
        return null;
    }

    private async Task<IActionResult> WithAttemptLeaseAsync(
        Func<PaymentCreateAttemptLease, Task<IActionResult>> action, CancellationToken ct)
    {
        PaymentCreateAttemptLease? lease = null;
        try
        {
            lease = await PaymentCreateAttemptLease.BeginAsync(_db, ct);
            await lease.LockActorPermissionsAsync(User.GetUserId()!.Value, ct);
            // Recheck current global payment permission within admission; selection is not a new company ACL.
            if (await RequireAttemptAccessAsync(ct) is { } denied) return denied;
            return await action(lease);
        }
        catch (PaymentCreateAttemptExpiredException) when (lease?.CommitStarted != true)
        {
            return AttemptGone();
        }
        catch (Exception error) when ((lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error))
            && PaymentCreateAttemptLease.IsPrecommitConflict(error))
        {
            return AttemptConflict();
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private ObjectResult AttemptGone() => StatusCode(StatusCodes.Status410Gone,
        new { message = "Попытка недоступна или уже закрыта. Не повторяйте создание без подтверждённого результата." });
    private ConflictObjectResult AttemptConflict() => Conflict(new
    { message = "Попытка или связанные данные изменились либо заняты. Сверьте результат сохранения." });
    private static PaymentCreateAttemptStateDto AttemptState(PaymentCreateAttempt item)
        => new(item.Id, item.State, item.Operation, item.SellerCompanyId, item.SellerBranchId,
            item.PrimaryPaymentId, item.CreatedAtUtc, item.ExpiresAtUtc,
            item.State == PaymentCreateAttempt.Committed ? item.CanonicalPaymentId : null, item.IdentityVersion);

    [HttpPost]
    public Task<IActionResult> Create([FromBody] Payment model, CancellationToken ct)
        => VersionedPaymentWriteBoundary.RunAsync(this, _db, null, model, creating: true, deleting: false, changesInvoice: false,
            scope => CreatePaymentCoreAsync(model, scope, ct), ct, deferInvoiceValidation: true);

    private async Task<IActionResult> CreatePaymentCoreAsync(
        Payment model, VersionedPaymentWriteBoundary.Scope scope, CancellationToken ct)
    {
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var now = DateTime.UtcNow;
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        if (model.DocumentKind == default)
        {
            model.DocumentKind = DocumentKind.Payment;
        }
        model.SystemNotes = model.SystemNotes ?? string.Empty;
        model.RescheduleCount = Math.Max(0, model.RescheduleCount);
        if (model.LastPaymentDate is null && model.PaidDate.HasValue)
        {
            model.LastPaymentDate = model.PaidDate.Value.Date;
        }

        var selectedInvoiceId = model.InvoiceId;
        await SyncInvoiceLinkAsync(model, ct);
        if (selectedInvoiceId is > 0 && model.InvoiceId != selectedInvoiceId) return NotFound();
        if (await ValidateInvoiceLinkAccessAsync(model, ct) is { } invalidInvoice) return invalidInvoice;
        await PopulateMissingExpenseAttributionFromCaseAsync(model, ct);
        var createValidationError = await ValidatePaymentAttributionAsync(model, ct);
        if (createValidationError is not null) return createValidationError;
        var rawPaidAmount = model.PaidAmount;
        PaymentDomainService.Normalize(model);
        PaymentDomainService.ApplyStatusRules(model, now, paymentTimeZone);
        PaymentAdvanceReviewService.ApplyAutomaticReviewState(model, now, HttpContext);
        var overpaymentValidationError = await ValidatePaymentOverpaymentHandlingAsync(model, rawPaidAmount, ct);
        if (overpaymentValidationError is not null) return overpaymentValidationError;

        if (await ValidateNewPaymentFinancialScopeAsync(model, rawPaidAmount, scope, ct) is { } financialError) return financialError;

        _db.Payments.Add(model);
        await _db.SaveChangesAsync(ct);
        var invoiceBefore = await CaptureInvoiceNotificationSnapshotAsync(model, ct);
        await PaymentAdvanceReviewService.SyncBalanceAsync(_db, model, now, HttpContext, ct);
        await ApplyPaymentOverpaymentHandlingAsync(model, rawPaidAmount, now, paymentTimeZone, ct);
        await SyncPaymentCashOrderAsync(model, now, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, model, now, ct);
        await _db.SaveChangesAsync(ct);
        scope.AfterCommit = async () =>
        {
            await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(_db, model, now, _notificationService, ct);
            PopulateAuditMetadata(_db, model);
            await NotifyPaymentAssignedAsync(model, ct);
            await NotifyInvoiceStateChangedAsync(model, invoiceBefore, ct);
        };
        return Created($"/api/payments/{model.Id}", model);
    }

    private async Task<IActionResult?> ValidateNewPaymentFinancialScopeAsync(Payment model, decimal rawPaidAmount,
        VersionedPaymentWriteBoundary.Scope scope, CancellationToken ct)
    {
        if (!model.InvoiceId.HasValue)
        {
            // An unresolved number must not silently turn a closed invoice's receipt
            // into an unrelated advance. Keep legacy linking rules; inspect only this owner/year.
            foreach (var candidate in BuildLookupCandidates(model))
            {
                var ownerClient = model.Type == PaymentType.Expense ? model.ExpenseClientId : model.ClientId;
                var ownerCompany = model.Type == PaymentType.Expense ? model.ExpenseLegalEntityId : model.LegalEntityId;
                if ((ownerClient is > 0) == (ownerCompany is > 0)) break;
                var normalized = candidate.Number.ToLowerInvariant(); var year = candidate.Date.Year;
                var invoiceIds = await _db.Payments.AsNoTracking().Where(p => p.DocumentKind == DocumentKind.Invoice && p.InvoiceId.HasValue
                    && p.Type == model.Type && p.ClientId == model.ClientId && p.LegalEntityId == model.LegalEntityId
                    && p.ExpenseClientId == model.ExpenseClientId && p.ExpenseLegalEntityId == model.ExpenseLegalEntityId)
                    .Where(p => model.ClientCaseId.HasValue
                        ? p.ClientCaseId == model.ClientCaseId || p.InvoiceClientCases.Any(link => link.ClientCaseId == model.ClientCaseId)
                        : p.ClientCaseId == null && !p.InvoiceClientCases.Any())
                    .Where(p => p.Invoice != null && p.Invoice.Number.ToLower() == normalized && p.Invoice.Date.Year == year
                        || p.Account != null && p.Account.ToLower() == normalized && (p.AccountDate ?? p.Date).Year == year
                        || p.LegacyAccountNumber != null && p.LegacyAccountNumber.ToLower() == normalized && (p.LegacyAccountDate ?? p.AccountDate ?? p.Date).Year == year)
                    .Select(p => p.InvoiceId!.Value).Distinct().Take(2).ToArrayAsync(ct);
                if (invoiceIds.Length == 0) continue;
                var visibility = await ResolvePaymentsVisibilityAsync(ct);
                if (visibility is null) return AccessDenied();
                var visibleHeaders = _db.Payments.AsNoTracking().ApplyPaymentVisibility(visibility).ApplyUnambiguousInvoiceLinks()
                    .Where(p => p.DocumentKind == DocumentKind.Invoice && p.InvoiceId.HasValue && invoiceIds.Contains(p.InvoiceId.Value));
                if (await visibleHeaders.Select(p => p.InvoiceId).Distinct().CountAsync(ct) != invoiceIds.Length) return NotFound();
                if (invoiceIds.Length > 1)
                    return Conflict(new { message = "По номеру найдено несколько счетов. Выберите нужный счёт перед сохранением платежа." });
                var headerId = await visibleHeaders.OrderBy(p => p.Status == PaymentStatus.Cancelled || p.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled)
                    .ThenBy(p => p.Id).Select(p => p.Id).FirstAsync(ct);
                await scope.LockPaymentAsync(headerId, ct);
                if (await PaymentCreditInvariantScope.IsCancellationProtectedAsync(_db, headerId, ct))
                    return BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
            }
        }
        if (model.InvoiceId.HasValue)
        {
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null) return AccessDenied();
            var headerId = await _db.Payments.AsNoTracking().ApplyPaymentVisibility(visibility).ApplyUnambiguousInvoiceLinks()
                .Where(p => p.InvoiceId == model.InvoiceId && p.DocumentKind == DocumentKind.Invoice)
                .OrderBy(p => p.Status == PaymentStatus.Cancelled || p.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled)
                .ThenBy(p => p.Id).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
            if (headerId.HasValue)
            {
                await scope.LockPaymentAsync(headerId.Value, ct);
                if (await PaymentCreditInvariantScope.IsCancellationProtectedAsync(_db, headerId.Value, ct))
                    return BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
            }
            else if (!visibility.SeesAll || !await _db.Invoices.AnyAsync(i => i.Id == model.InvoiceId && !i.Payments.Any(), ct)) return NotFound();
        }
        if (model.Type == PaymentType.Income && CalculateOverpaymentAmount(model, rawPaidAmount) > AmountTolerance
            && model.OverpaymentHandling == PaymentOverpaymentHandling.NextInstallment)
        {
            var target = await FindOverpaymentTargetInstallmentPaymentAsync(model, track: false, ct);
            if (target is null) return BadRequest(new { message = "Следующий платёж рассрочки для зачёта переплаты не найден." });
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null) return AccessDenied();
            if (!await CanAccessPaymentAsync(target.Id, visibility, ct)) return NotFound();
            await scope.LockPaymentAsync(target.Id, ct);
            if (await PaymentCreditInvariantScope.IsCancellationProtectedAsync(_db, target.Id, ct))
                return BadRequest(new { message = PaymentCreditInvariantScope.CancellationProtectedMessage });
            // Freeze the selected row before the new payment receives an identity.
            model.OverpaymentTargetPaymentId = target.Id;
            if (await ValidatePaymentOverpaymentHandlingAsync(model, rawPaidAmount, ct) is { } invalidTarget) return invalidTarget;
        }
        return null;
    }

    [HttpPut("{id:int}")]
    public Task<IActionResult> Update(int id, [FromBody] Payment model, CancellationToken ct)
        => RunPaymentWriteAsync(id, scope => UpdateCoreAsync(id, model, scope, ct), ct, allowClosedMetadata: true);

    private async Task<IActionResult> UpdateCoreAsync(int id, Payment model, PaymentWriteScope scope, CancellationToken ct)
    {
        if (await RequirePaymentsEditAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }
        if (!await CanAccessPaymentAsync(id, visibility, ct))
        {
            return NotFound();
        }

        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        if (PaymentAdvanceReviewService.NormalizeUpdateInput(e, model) is { } reviewError)
            return BadRequest(new { message = reviewError });
        if (e.AdvanceReviewStatus == PaymentAdvanceReviewStatus.Refunded)
            return await UpdateClosedReceiptMetadataAsync(e, model, scope, ct, PaymentAdvanceReviewService.RefundedEditMessage);
        if (scope.CancellationProtected) return await UpdateClosedReceiptMetadataAsync(e, model, scope, ct);
        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(e))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.UpdateBlockedMessage });
        }

        var transferProtectionError = await ValidatePartialRemainderUpdateAsync(e, model, ct);
        if (transferProtectionError is not null) return transferProtectionError;
        var originalTransferPayment = (Payment)_db.Entry(e).CurrentValues.ToObject();

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);

        var originalDueDate = e.Date.Date;
        var originalPlannedDate = (e.PlannedDate == default ? e.Date : e.PlannedDate).Date;
        var originalPaidAmount = e.PaidAmount;
        var originalStatus = e.Status;
        var originalAmount = e.Amount;
        var originalPaymentSourceId = e.PaymentSourceId;
        var originalRescheduleCount = Math.Max(0, e.RescheduleCount);
        var originalSystemNotes = e.SystemNotes ?? string.Empty;
        if (PaymentCreateInputRules.ValidateSystemNotesChange(originalSystemNotes, model.SystemNotes) is { } notesError)
            return BadRequest(new { message = notesError });
        var originalBalanceBinding = PaymentBalanceHistoryRules.Binding.From(e);
        var originalInvoiceId = e.InvoiceId;
        var originalClientId = e.ClientId;
        var originalLegalEntityId = e.LegalEntityId;
        var originalType = e.Type;

        if (e.PlannedDate == default)
        {
            e.PlannedDate = originalPlannedDate;
        }

        e.Date = model.Date;
        if (model.PlannedDate != default)
        {
            e.PlannedDate = model.PlannedDate;
        }
        SyncPlannedDateWithChangedDueDate(e, originalDueDate, originalPlannedDate, model);
        e.Amount = model.Amount;
        e.Type = model.Type;
        e.Status = model.Status;
        e.Description = model.Description ?? string.Empty;
        e.Notes = model.Notes ?? string.Empty;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        e.ExpenseClientId = model.ExpenseClientId;
        e.ExpenseLegalEntityId = model.ExpenseLegalEntityId;
        e.ClientCaseId = model.ClientCaseId;
        e.ContractId = model.ContractId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.ServiceId = model.ServiceId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.InvoiceId = model.InvoiceId;
        e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        e.DocumentKind = model.DocumentKind == default ? DocumentKind.Payment : model.DocumentKind;
        e.AccountDate = model.AccountDate;
        e.PaidDate = model.PaidDate;
        e.PaymentOrderNumber = model.PaymentOrderNumber;
        e.PaymentDocumentDate = model.PaymentDocumentDate;
        e.OverpaymentHandling = model.OverpaymentHandling;
        e.OverpaymentTargetPaymentId = model.OverpaymentTargetPaymentId;
        e.OverpaymentComment = model.OverpaymentComment;
        PaymentAdvanceReviewService.ApplyValidatedUpdateState(e, model);
        var rawPaidAmount = model.PaidAmount;
        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, e.Id, ct);
        if (appliedCreditAmount > AmountTolerance
            && (e.ClientId != originalClientId || e.LegalEntityId != originalLegalEntityId || e.Type != originalType))
            return BadRequest(new { message = "Нельзя менять контрагента или направление платежа с зачтённым балансом." });
        if (e.Amount + AmountTolerance < appliedCreditAmount)
            return BadRequest(new { message = "Сумма платежа не может быть меньше зачтённого баланса контрагента." });
        if (rawPaidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }
        e.PaidAmount = rawPaidAmount;
        e.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? e.LastPaymentDate;
        e.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        await SyncInvoiceLinkAsync(e, ct);
        if (await ValidateInvoiceLinkAccessAsync(e, ct) is { } invalidInvoice) return invalidInvoice;
        await PopulateMissingExpenseAttributionFromCaseAsync(e, ct);
        var updateValidationError = await ValidatePaymentAttributionAsync(e, ct);
        if (updateValidationError is not null) return updateValidationError;
        if (await PaymentBalanceHistoryRules.ValidateBindingAsync(_db, e, originalBalanceBinding, ct) is { } historyError)
            return BadRequest(new { message = historyError });
        PaymentDomainService.Normalize(e);

        if (e.LastPaymentDate is null && e.PaidAmount > 0m)
        {
            e.LastPaymentDate = (model.LastPaymentDate ?? model.PaidDate)?.Date;
        }

        PaymentDomainService.ApplyStatusRules(e, now, paymentTimeZone);
        PaymentAdvanceReviewService.ApplyAutomaticReviewState(e, now, HttpContext);
        SyncInstallmentScheduleMetadataDate(e);

        var rescheduleCount = originalRescheduleCount;
        var plannedDateChanged = model.PlannedDate != default && originalPlannedDate != e.PlannedDate.Date;
        if (plannedDateChanged)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {e.PlannedDate:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        var userChangedDueDate = model.Date.Date != originalDueDate;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {e.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }
        else if (originalDueDate != e.Date.Date)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа автоматически скорректирована на {e.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        if (Math.Abs(originalPaidAmount - e.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {e.PaidAmount.ToString("N2", culture)}",
                now,
                actor,
                paymentTimeZone);
        }

        if (originalStatus != e.Status)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {e.Status.ToDisplayString()}",
                now,
                actor,
                paymentTimeZone);
        }

        e.RescheduleCount = rescheduleCount;

        var overpaymentValidationError = await ValidatePaymentOverpaymentHandlingAsync(e, rawPaidAmount, ct);
        if (overpaymentValidationError is not null) return overpaymentValidationError;

        if (await PaymentBalanceHistoryRules.ValidateSyncAsync(_db, e, ct, rawPaidAmount) is { } advanceHistoryError)
            return BadRequest(new { message = advanceHistoryError });
        var invoiceBefore = await CaptureInvoiceNotificationSnapshotAsync(e, ct);
        await ApplyPaymentOverpaymentHandlingAsync(e, rawPaidAmount, now, paymentTimeZone, ct);
        if (e.PaidAmount + AmountTolerance < appliedCreditAmount)
            return BadRequest(new { message = "Оплата не может быть меньше зачтённого баланса контрагента." });
        await PaymentAdvanceReviewService.SyncBalanceAsync(_db, e, now, HttpContext, ct);
        await SyncPaymentCashOrderAsync(e, now, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, e, now, ct);
        if (originalInvoiceId.HasValue
            && originalInvoiceId.Value > 0
            && originalInvoiceId != e.InvoiceId)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, now, ct);
        }

        if (await PaymentRemainderWriteRules.ValidateUpdateAsync(_db, originalTransferPayment, e, ct) is { } normalizedTransferError)
            return Conflict(new { message = normalizedTransferError });

        await _db.SaveChangesAsync(ct);
        scope.AfterCommit = async () =>
        {
            await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(_db, e, now, _notificationService, ct);
            PopulateAuditMetadata(_db, e);
            await NotifyPaymentChangedAsync(
                e,
                BuildPaymentChangeLabels(
                    originalStatus,
                    e.Status,
                    originalDueDate,
                    e.Date.Date,
                    originalAmount,
                    e.Amount,
                    originalPaymentSourceId,
                    e.PaymentSourceId),
                ct);
            await NotifyInvoiceStateChangedAsync(e, invoiceBefore, ct);
        };
        return Ok(e);
    }

    private async Task<IActionResult> UpdateClosedReceiptMetadataAsync(Payment current, Payment model, PaymentWriteScope scope, CancellationToken ct, string? financialMessage = null)
    {
        if (await ValidateInvoiceLinkAccessAsync(current, ct) is { } invalidInvoice) return invalidInvoice;
        if (current.DocumentKind != DocumentKind.Payment || InvoiceManagedPaymentRules.IsInvoiceManagedPayment(current)
            || CancelledReceiptAnnotations.HasFinancialChanges(current, model)
            || (!string.IsNullOrEmpty(model.SystemNotes) && model.SystemNotes != current.SystemNotes))
            return BadRequest(new { message = financialMessage ?? PaymentCreditInvariantScope.CancellationProtectedMessage });

        // This path deliberately saves only ordinary receipt annotations. Running
        // normalization, balance/cash synchronization or invoice refresh here could
        // change historical money and dates even when only a note was submitted.
        current.Description = model.Description ?? string.Empty;
        current.Notes = model.Notes ?? string.Empty;
        await _db.SaveChangesAsync(ct);
        scope.ClosedReceiptMetadataOnly = true;
        scope.AfterCommit = () => { PopulateAuditMetadata(_db, current); return Task.CompletedTask; };
        return Ok(current);
    }

    [HttpPost("{id:int}/status")]
    public Task<IActionResult> UpdateStatus(int id, [FromBody] UpdatePaymentStatusRequest request, CancellationToken ct)
        => RunPaymentWriteAsync(id, scope => UpdateStatusCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> UpdateStatusCoreAsync(int id, UpdatePaymentStatusRequest request, PaymentWriteScope scope, CancellationToken ct)
    {
        if (await RequirePaymentsEditAsync(ct) is { } forbidden) return forbidden;

        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!Enum.TryParse<PaymentStatus>(request.Status, ignoreCase: true, out var nextStatus))
        {
            return BadRequest("Unknown payment status.");
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }
        if (!await CanAccessPaymentAsync(id, visibility, ct))
        {
            return NotFound();
        }

        var payment = await _db.Payments.FindAsync(new object[] { id }, ct);
        if (payment is null) return NotFound();
        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(payment))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.UpdateBlockedMessage });
        }

        if (await PaymentRemainderWriteRules.ValidateStatusAsync(_db, payment, nextStatus, ct) is { } remainderError)
            return Conflict(new { message = remainderError });

        var now = DateTime.UtcNow;
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        var originalStatus = payment.Status;
        var reason = Normalize(request.Reason);

        if (originalStatus != nextStatus)
        {
            var reasonError = await ValidateFinancialActionReasonAsync(reason, "изменения статуса платежа", ct);
            if (reasonError is not null)
            {
                return reasonError;
            }

            ApplyAuditReason(reason);
        }

        payment.Status = nextStatus;

        if (nextStatus == PaymentStatus.Completed)
        {
            var paidDate = (request.PaidDate ?? payment.PaidDate ?? payment.LastPaymentDate ?? now).Date;
            payment.PaidAmount = payment.Amount;
            payment.IsPaid = true;
            payment.PaidDate = paidDate;
            payment.LastPaymentDate = paidDate;
            payment.Date = paidDate;
        }
        else
        {
            payment.IsPaid = false;
            payment.PaidDate = null;

            if (payment.PaidAmount >= payment.Amount - AmountTolerance)
            {
                payment.PaidAmount = 0m;
                payment.LastPaymentDate = null;
            }

            if (nextStatus == PaymentStatus.Cancelled)
            {
                payment.PaidAmount = 0m;
                payment.LastPaymentDate = null;
            }
        }

        var appliedCredit = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, payment.Id, ct);
        if (payment.PaidAmount + AmountTolerance < appliedCredit)
            return BadRequest(new { message = "Нельзя уменьшить оплату ниже суммы, зачтённой с баланса контрагента." });

        PaymentDomainService.Normalize(payment);
        PaymentAdvanceReviewService.ApplyAutomaticReviewState(payment, now, HttpContext);

        if (originalStatus != payment.Status)
        {
            PaymentDomainService.AppendSystemNote(
                payment,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {payment.Status.ToDisplayString()}",
                now,
                actor,
                paymentTimeZone);
        }

        if (await PaymentBalanceHistoryRules.ValidateSyncAsync(_db, payment, ct) is { } advanceHistoryError)
            return BadRequest(new { message = advanceHistoryError });
        var invoiceBefore = await CaptureInvoiceNotificationSnapshotAsync(payment, ct);
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, payment, payment.PaidAmount, now, HttpContext, ct);
        await PaymentAdvanceReviewService.SyncBalanceAsync(_db, payment, now, HttpContext, ct);
        await SyncPaymentCashOrderAsync(payment, now, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, payment, now, ct);
        await _db.SaveChangesAsync(ct);
        scope.AfterCommit = async () =>
        {
            await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(_db, payment, now, _notificationService, ct);
            PopulateAuditMetadata(_db, payment);
            if (originalStatus != payment.Status)
            {
                await NotifyPaymentChangedAsync(
                    payment,
                    [$"Статус: {originalStatus.ToDisplayString()} -> {payment.Status.ToDisplayString()}"],
                    ct);
            }
            await NotifyInvoiceStateChangedAsync(payment, invoiceBefore, ct);
        };
        return Ok(payment);
    }

    [HttpPost("{id:int}/advance-review/accept")]
    public Task<IActionResult> AcceptAdvance(int id, [FromBody] PaymentAdvanceReviewRequest? request, CancellationToken ct)
        => RunPaymentWriteAsync(id, scope => AcceptAdvanceCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> AcceptAdvanceCoreAsync(int id, PaymentAdvanceReviewRequest? request, PaymentWriteScope scope, CancellationToken ct)
    {
        var payment = await _db.Payments.FindAsync(new object[] { id }, ct);
        if (payment is null) return NotFound();

        if (await PaymentBalanceHistoryRules.ValidateReviewAsync(_db, payment, ct) is { } reviewHistoryError)
            return BadRequest(new { message = reviewHistoryError });
        PaymentDomainService.Normalize(payment);
        var expectedCredit = PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment) ? PaymentAdvanceReviewService.GetAdvanceAmount(payment) : 0m;
        if (await PaymentBalanceHistoryRules.ValidateAdvanceCreditAsync(_db, payment, expectedCredit, ct) is { } advanceHistoryError)
            return BadRequest(new { message = advanceHistoryError });

        var now = DateTime.UtcNow;
        var result = await PaymentAdvanceReviewService.AcceptAsAdvanceAsync(
            _db,
            payment,
            request?.Reason,
            now,
            HttpContext,
            ct);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        _db.SetAuditReason($"Контроль аванса: платеж принят как аванс. {payment.AdvanceReviewReason}");
        await _db.SaveChangesAsync(ct);
        scope.AfterCommit = () => { PopulateAuditMetadata(_db, payment); return Task.CompletedTask; };
        return Ok(payment);
    }

    [HttpPost("{id:int}/advance-review/ignore")]
    public Task<IActionResult> IgnoreAdvance(int id, [FromBody] PaymentAdvanceReviewRequest? request, CancellationToken ct)
        => RunPaymentWriteAsync(id, scope => IgnoreAdvanceCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> IgnoreAdvanceCoreAsync(int id, PaymentAdvanceReviewRequest? request, PaymentWriteScope scope, CancellationToken ct)
    {
        var payment = await _db.Payments.FindAsync(new object[] { id }, ct);
        if (payment is null) return NotFound();

        if (await PaymentBalanceHistoryRules.ValidateReviewAsync(_db, payment, ct) is { } reviewHistoryError)
            return BadRequest(new { message = reviewHistoryError });
        if (await PaymentBalanceHistoryRules.ValidateAdvanceCreditAsync(_db, payment, 0m, ct) is { } advanceHistoryError)
            return BadRequest(new { message = advanceHistoryError });

        var now = DateTime.UtcNow;
        var result = await PaymentAdvanceReviewService.IgnoreAsync(
            _db,
            payment,
            request?.Reason,
            now,
            HttpContext,
            ct);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        _db.SetAuditReason($"Контроль аванса: платеж исключен из контроля. {payment.AdvanceReviewReason}");
        await _db.SaveChangesAsync(ct);
        scope.AfterCommit = () => { PopulateAuditMetadata(_db, payment); return Task.CompletedTask; };
        return Ok(payment);
    }

    [HttpPost("{id:int}/advance-review/refund")]
    public Task<IActionResult> RefundAdvance(int id, [FromBody] PaymentAdvanceReviewRequest? request, CancellationToken ct)
        => RunPaymentWriteAsync(id, scope => RefundAdvanceCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> RefundAdvanceCoreAsync(int id, PaymentAdvanceReviewRequest? request, PaymentWriteScope scope, CancellationToken ct)
    {
        var payment = await _db.Payments
            .Include(item => item.PaymentSource)
            .Include(item => item.CashOrders)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (payment is null) return NotFound();

        if (await PaymentBalanceHistoryRules.ValidateReviewAsync(_db, payment, ct) is { } reviewHistoryError)
            return BadRequest(new { message = reviewHistoryError });

        var now = DateTime.UtcNow;
        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? "Возврат нераспределенного аванса клиенту"
            : request!.Reason.Trim();
        var result = await ClientBalanceService.RefundUnallocatedAdvanceAsync(
            _db,
            payment,
            $"Возврат нераспределенного аванса по платежу #{payment.Id}. {reason}",
            now,
            HttpContext,
            ct);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        await UpsertUnallocatedAdvanceRefundOperationAsync(payment, result.RefundedAmount, reason, now, ct);

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Refunded;
        payment.AdvanceReviewReason = reason;
        payment.AdvanceReviewedAt = now;
        payment.AdvanceReviewedByUserId = HttpContext.User.GetUserId();
        payment.AdvanceReviewedByUserName = HttpContext.User.GetDisplayName();

        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        PaymentDomainService.AppendSystemNote(
            payment,
            $"Нераспределенный аванс возвращен клиенту: {result.RefundedAmount:N2}. Причина: {reason}",
            now,
            HttpContext.User.GetDisplayName(),
            paymentTimeZone);

        _db.SetAuditReason($"Контроль аванса: аванс #{payment.Id} возвращен клиенту на сумму {result.RefundedAmount:N2}. {reason}");
        await _db.SaveChangesAsync(ct);
        scope.AfterCommit = () => { PopulateAuditMetadata(_db, payment); return Task.CompletedTask; };
        return Ok(payment);
    }

    [HttpGet("advance-review/candidates")]
    public async Task<IActionResult> GetAdvanceReviewCandidates([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }

        limit = Math.Clamp(limit, 1, 500);
        var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
        var query = _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .Where(candidatePredicate)
            .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None
                || payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision);

        var total = await query.CountAsync(ct);
        var totalAmount = await query
            .SumAsync(payment => payment.PaidAmount > 0.009m ? payment.PaidAmount : payment.Amount, ct);

        var items = await query
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .Take(limit)
            .Select(payment => new PaymentAdvanceReviewCandidateDto
            {
                Id = payment.Id,
                Date = payment.Date,
                Amount = payment.Amount,
                PaidAmount = payment.PaidAmount,
                AdvanceAmount = payment.PaidAmount > 0.009m ? payment.PaidAmount : payment.Amount,
                Description = payment.Description,
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                CounterpartyName = payment.Client != null
                    ? payment.Client.Name
                    : payment.LegalEntity != null
                        ? payment.LegalEntity.ShortName != string.Empty
                            ? payment.LegalEntity.ShortName
                            : payment.LegalEntity.FullName
                        : payment.CounterpartyName,
                ClientCaseId = payment.ClientCaseId,
                ContractId = payment.ContractId,
                Status = payment.Status,
                AdvanceReviewStatus = payment.AdvanceReviewStatus,
            })
            .ToListAsync(ct);

        return Ok(new PaymentAdvanceReviewCandidateReportDto
        {
            Total = total,
            TotalAmount = totalAmount,
            Limit = limit,
            Items = items,
        });
    }

    [HttpGet("advance-review/diagnostics")]
    public async Task<IActionResult> GetAdvanceReviewDiagnostics(
        [FromQuery] string? q = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] decimal? amount = null,
        [FromQuery] decimal amountTolerance = AmountTolerance,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }

        var queryText = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (queryText is null && !from.HasValue && !to.HasValue && !amount.HasValue)
        {
            return BadRequest(new { message = "Укажите текст поиска, период или сумму платежа для диагностики аванса." });
        }

        limit = Math.Clamp(limit, 1, 200);
        amountTolerance = Math.Max(0m, amountTolerance);

        var query = _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .Include(payment => payment.Client)
            .Include(payment => payment.LegalEntity)
            .Include(payment => payment.Acts)
            .AsQueryable();

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(payment => payment.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(payment => payment.Date <= toDate);
        }

        if (amount.HasValue)
        {
            var minAmount = amount.Value - amountTolerance;
            var maxAmount = amount.Value + amountTolerance;
            query = query.Where(payment =>
                (payment.PaidAmount > AmountTolerance ? payment.PaidAmount : payment.Amount) >= minAmount
                && (payment.PaidAmount > AmountTolerance ? payment.PaidAmount : payment.Amount) <= maxAmount);
        }

        if (queryText is not null)
        {
            query = query.Where(payment =>
                (payment.Description != null && payment.Description.Contains(queryText))
                || (payment.CounterpartyName != null && payment.CounterpartyName.Contains(queryText))
                || (payment.Account != null && payment.Account.Contains(queryText))
                || (payment.LegacyAccountNumber != null && payment.LegacyAccountNumber.Contains(queryText))
                || (payment.Client != null && payment.Client.Name.Contains(queryText))
                || (payment.LegalEntity != null && ((payment.LegalEntity.ShortName != null && payment.LegalEntity.ShortName.Contains(queryText))
                    || (payment.LegalEntity.FullName != null && payment.LegalEntity.FullName.Contains(queryText)))));
        }

        var total = await query.CountAsync(ct);
        var totalAmount = await query
            .SumAsync(payment => payment.PaidAmount > AmountTolerance ? payment.PaidAmount : payment.Amount, ct);

        var payments = await query
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .Take(limit)
            .ToListAsync(ct);

        var paymentIds = payments.Select(payment => payment.Id).ToList();
        var balanceByPaymentId = paymentIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.ClientBalanceTransactions
                .AsNoTracking()
                .Where(item => item.PaymentId.HasValue
                    && paymentIds.Contains(item.PaymentId.Value)
                    && item.Type == ClientBalanceTransactionType.UnallocatedAdvance)
                .GroupBy(item => item.PaymentId!.Value)
                .Select(group => new
                {
                    PaymentId = group.Key,
                    Amount = group.Sum(item => item.Direction == ClientBalanceDirection.Credit ? item.Amount : -item.Amount),
                })
                .ToDictionaryAsync(item => item.PaymentId, item => item.Amount, ct);

        var items = payments
            .Select(payment =>
            {
                var unallocatedAdvanceBalance = balanceByPaymentId.GetValueOrDefault(payment.Id);
                return new PaymentAdvanceReviewDiagnosticItemDto
                {
                    Id = payment.Id,
                    Date = payment.Date,
                    Amount = payment.Amount,
                    PaidAmount = payment.PaidAmount,
                    AdvanceAmount = PaymentAdvanceReviewService.GetAdvanceAmount(payment),
                    Description = payment.Description,
                    ClientId = payment.ClientId,
                    LegalEntityId = payment.LegalEntityId,
                    CounterpartyName = payment.Client?.Name
                        ?? (!string.IsNullOrWhiteSpace(payment.LegalEntity?.ShortName)
                            ? payment.LegalEntity.ShortName
                            : payment.LegalEntity?.FullName)
                        ?? payment.CounterpartyName,
                    ClientCaseId = payment.ClientCaseId,
                    ContractId = payment.ContractId,
                    Status = payment.Status,
                    Type = payment.Type,
                    AdvanceReviewStatus = payment.AdvanceReviewStatus,
                    IsUnallocatedAdvanceCandidate = PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment),
                    HasInvoiceLink = payment.InvoiceId.HasValue,
                    HasActLink = payment.Acts.Count > 0,
                    HasAccountNumber = !string.IsNullOrWhiteSpace(payment.Account)
                        || !string.IsNullOrWhiteSpace(payment.LegacyAccountNumber),
                    HasUnallocatedAdvanceBalanceTransaction = unallocatedAdvanceBalance != 0m,
                    UnallocatedAdvanceBalanceAmount = unallocatedAdvanceBalance,
                };
            })
            .ToList();

        return Ok(new PaymentAdvanceReviewDiagnosticsResponseDto
        {
            Total = total,
            TotalAmount = totalAmount,
            Limit = limit,
            Query = queryText,
            From = from?.Date,
            To = to?.Date,
            Amount = amount,
            AmountTolerance = amountTolerance,
            Items = items,
        });
    }

    private sealed class AdvanceReviewBatchScope(BalanceApplicationLease lease, DataVisibilityAccess visibility)
    {
        public BalanceApplicationLease Lease { get; } = lease;
        public DataVisibilityAccess Visibility { get; } = visibility;
        public Func<Task>? AfterCommit { get; set; }
    }

    private async Task<IActionResult> RunAdvanceReviewBatchAsync(
        Func<AdvanceReviewBatchScope, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (User?.Identity?.IsAuthenticated != true || actorId is not > 0) return Unauthorized();
        BalanceApplicationLease? lease = null;
        AdvanceReviewBatchScope? scope = null;
        IActionResult result;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(_db, actorId.Value, ct);
            if (_db.Database.IsNpgsql()) await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return Unauthorized();
            if (await RequirePaymentsEditAsync(ct) is { } forbidden) return forbidden;
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null) return AccessDenied();
            scope = new(lease, visibility);
            result = await write(scope);
            if (result is OkObjectResult) await lease.CommitAsync(ct);
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Платежи изменились или сейчас заняты. Обновите список и повторите действие." });
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
        if (result is OkObjectResult && scope?.AfterCommit is { } notify) await notify();
        return result;
    }

    private async Task<List<Payment>> LockAdvanceReviewBatchAsync(
        IQueryable<Payment> query, BalanceApplicationLease lease, int limit, CancellationToken ct)
    {
        var ids = await query.AsNoTracking().OrderBy(payment => payment.Date).ThenBy(payment => payment.Id)
            .Select(payment => payment.Id).Take(limit).ToArrayAsync(ct);
        await lease.LockPaymentsAsync(ids, ct);
        var payments = await query.Where(payment => ids.Contains(payment.Id))
            .Include(payment => payment.Client).Include(payment => payment.LegalEntity)
            .OrderBy(payment => payment.Date).ThenBy(payment => payment.Id).ToListAsync(ct);
        if (payments.Count != ids.Length) throw new BalanceApplicationConflictException();
        return payments;
    }

    [HttpPost("advance-review/backfill")]
    public async Task<IActionResult> BackfillAdvanceReviewCandidates(
        [FromQuery] bool confirm = false,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return BadRequest(new { message = "Backfill requires confirm=true." });
        }

        return await RunAdvanceReviewBatchAsync(scope => BackfillAdvanceReviewCandidatesCoreAsync(scope, limit, ct), ct);
    }

    private async Task<IActionResult> BackfillAdvanceReviewCandidatesCoreAsync(AdvanceReviewBatchScope scope, int limit, CancellationToken ct)
    {
        var visibility = scope.Visibility;

        limit = Math.Clamp(limit, 1, 500);
        var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
        var query = _db.Payments
            .ApplyPaymentVisibility(visibility)
            .Where(candidatePredicate)
            .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None);

        var eligible = await query.CountAsync(ct);
        var totalAmount = await query
            .SumAsync(payment => payment.PaidAmount > 0.009m ? payment.PaidAmount : payment.Amount, ct);

        var payments = await LockAdvanceReviewBatchAsync(query, scope.Lease, limit, ct);

        foreach (var payment in payments)
        {
            payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
            payment.AdvanceReviewReason = null;
            payment.AdvanceReviewedAt = null;
            payment.AdvanceReviewedByUserId = null;
            payment.AdvanceReviewedByUserName = null;
        }

        if (payments.Count > 0)
        {
            _db.SetAuditReason($"Backfill контроля авансов: {payments.Count} старых платежей поставлены на разбор без изменения денежных проводок.");
            await _db.SaveChangesAsync(ct);
            if (_notificationService is not null)
            {
                var ids = payments.Select(payment => payment.Id).ToArray();
                scope.AfterCommit = async () =>
                {
                    var committedPayments = await _db.Payments.AsNoTracking()
                        .Where(payment => ids.Contains(payment.Id)
                            && payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)
                        .Where(PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate())
                        .Include(payment => payment.Client).Include(payment => payment.LegalEntity)
                        .ToListAsync(ct);
                    foreach (var payment in committedPayments)
                        await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(_db, payment, DateTime.UtcNow, _notificationService, ct);
                };
            }
        }

        var items = payments
            .Select(payment => new PaymentAdvanceReviewCandidateDto
            {
                Id = payment.Id,
                Date = payment.Date,
                Amount = payment.Amount,
                PaidAmount = payment.PaidAmount,
                AdvanceAmount = payment.PaidAmount > 0.009m ? payment.PaidAmount : payment.Amount,
                Description = payment.Description,
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                CounterpartyName = payment.Client?.Name
                    ?? (!string.IsNullOrWhiteSpace(payment.LegalEntity?.ShortName)
                        ? payment.LegalEntity.ShortName
                        : payment.LegalEntity?.FullName)
                    ?? payment.CounterpartyName,
                ClientCaseId = payment.ClientCaseId,
                ContractId = payment.ContractId,
                Status = payment.Status,
                AdvanceReviewStatus = payment.AdvanceReviewStatus,
            })
            .ToList();

        return Ok(new PaymentAdvanceReviewBackfillResponseDto
        {
            Eligible = eligible,
            Updated = payments.Count,
            TotalAmount = totalAmount,
            Limit = limit,
            Items = items,
        });
    }

    [HttpPost("advance-review/backfill/rollback")]
    public async Task<IActionResult> RollbackAdvanceReviewBackfill(
        [FromQuery] bool confirm = false,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return BadRequest(new { message = "Backfill rollback requires confirm=true." });
        }

        return await RunAdvanceReviewBatchAsync(scope => RollbackAdvanceReviewBackfillCoreAsync(scope, limit, ct), ct);
    }

    private async Task<IActionResult> RollbackAdvanceReviewBackfillCoreAsync(AdvanceReviewBatchScope scope, int limit, CancellationToken ct)
    {
        var visibility = scope.Visibility;

        limit = Math.Clamp(limit, 1, 500);
        // Preserve any later edit or restoration, including timestamp ties.
        var history = _db.ObjectAuditEvents
            .Where(item => item.ObjectType == "payment");

        var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
        var query = _db.Payments
            .ApplyPaymentVisibility(visibility)
            .Where(candidatePredicate)
            .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)
            .Where(payment => history.Any(item => item.ObjectId == payment.Id && item.Action == "Updated"
                && item.MetadataJson != null
                && item.MetadataJson.Contains("\"reason\":\"Backfill ")
                && item.MetadataJson.Contains("\"AdvanceReviewStatus\"")
                && !history.Any(later => later.ObjectId == item.ObjectId
                    && (later.CreatedAt > item.CreatedAt
                        || (later.CreatedAt == item.CreatedAt && later.Id > item.Id)))));

        var eligible = await query.CountAsync(ct);
        var totalAmount = await query
            .SumAsync(payment => payment.PaidAmount > 0.009m ? payment.PaidAmount : payment.Amount, ct);

        var payments = await LockAdvanceReviewBatchAsync(query, scope.Lease, limit, ct);

        foreach (var payment in payments)
        {
            payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;
            payment.AdvanceReviewReason = null;
            payment.AdvanceReviewedAt = null;
            payment.AdvanceReviewedByUserId = null;
            payment.AdvanceReviewedByUserName = null;
        }

        if (payments.Count > 0)
        {
            _db.SetAuditReason($"Rollback backfill контроля авансов: {payments.Count} платежей возвращены из очереди разбора без изменения денежных проводок.");
            await _db.SaveChangesAsync(ct);
        }

        var items = payments
            .Select(payment => new PaymentAdvanceReviewCandidateDto
            {
                Id = payment.Id,
                Date = payment.Date,
                Amount = payment.Amount,
                PaidAmount = payment.PaidAmount,
                AdvanceAmount = payment.PaidAmount > 0.009m ? payment.PaidAmount : payment.Amount,
                Description = payment.Description,
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                CounterpartyName = payment.Client?.Name
                    ?? (!string.IsNullOrWhiteSpace(payment.LegalEntity?.ShortName)
                        ? payment.LegalEntity.ShortName
                        : payment.LegalEntity?.FullName)
                    ?? payment.CounterpartyName,
                ClientCaseId = payment.ClientCaseId,
                ContractId = payment.ContractId,
                Status = payment.Status,
                AdvanceReviewStatus = payment.AdvanceReviewStatus,
            })
            .ToList();

        return Ok(new PaymentAdvanceReviewBackfillRollbackResponseDto
        {
            Eligible = eligible,
            Updated = payments.Count,
            TotalAmount = totalAmount,
            Limit = limit,
            Items = items,
        });
    }

    [HttpPut("{id:int}/invoice-managed")]
    public Task<IActionResult> UpdateInvoiceManaged(int id, [FromBody] Payment model, CancellationToken ct)
        => RunPaymentWriteAsync(id, scope => UpdateInvoiceManagedCoreAsync(id, model, ct), ct);

    private async Task<IActionResult> UpdateInvoiceManagedCoreAsync(int id, Payment model, CancellationToken ct)
    {
        if (await RequirePaymentsEditAsync(ct) is { } forbidden) return forbidden;
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }
        if (!await CanAccessPaymentAsync(id, visibility, ct))
        {
            return NotFound();
        }

        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        if (!InvoiceManagedPaymentRules.IsInvoiceManagedPayment(e))
        {
            return BadRequest(new { message = "Payment is not invoice-managed." });
        }

        var transferProtectionError = await ValidatePartialRemainderUpdateAsync(e, model, ct);
        if (transferProtectionError is not null) return transferProtectionError;
        var originalTransferPayment = (Payment)_db.Entry(e).CurrentValues.ToObject();

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != e.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);

        var originalDueDate = e.Date.Date;
        var originalPlannedDate = (e.PlannedDate == default ? e.Date : e.PlannedDate).Date;
        var originalPaidAmount = e.PaidAmount;
        var originalStatus = e.Status;
        var originalRescheduleCount = Math.Max(0, e.RescheduleCount);
        var originalSystemNotes = e.SystemNotes ?? string.Empty;
        if (PaymentCreateInputRules.ValidateSystemNotesChange(originalSystemNotes, model.SystemNotes) is { } notesError)
            return BadRequest(new { message = notesError });
        var originalNotes = e.Notes;
        var originalClientId = e.ClientId;
        var originalLegalEntityId = e.LegalEntityId;

        if (e.PlannedDate == default)
        {
            e.PlannedDate = originalPlannedDate;
        }

        var immutableInvoiceId = e.InvoiceId;
        var immutableDocumentKind = e.DocumentKind;
        var immutableAccount = e.Account;
        var immutableAccountDate = e.AccountDate;
        var immutableLegacyAccountNumber = e.LegacyAccountNumber;
        var immutableLegacyAccountDate = e.LegacyAccountDate;
        var immutableType = e.Type;

        e.Date = model.Date;
        if (model.PlannedDate != default)
        {
            e.PlannedDate = model.PlannedDate;
        }
        SyncPlannedDateWithChangedDueDate(e, originalDueDate, originalPlannedDate, model);
        e.Amount = model.Amount;
        e.Status = model.Status;
        e.Description = model.Description ?? string.Empty;
        e.Notes = model.Notes ?? string.Empty;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        e.ExpenseClientId = model.ExpenseClientId;
        e.ExpenseLegalEntityId = model.ExpenseLegalEntityId;
        e.ClientCaseId = model.ClientCaseId;
        e.ContractId = model.ContractId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.ServiceId = model.ServiceId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.PaidDate = model.PaidDate;
        e.PaymentOrderNumber = model.PaymentOrderNumber;
        e.PaymentDocumentDate = model.PaymentDocumentDate;
        e.OverpaymentHandling = model.OverpaymentHandling;
        e.OverpaymentTargetPaymentId = model.OverpaymentTargetPaymentId;
        e.OverpaymentComment = model.OverpaymentComment;
        var rawPaidAmount = model.PaidAmount;
        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, e.Id, ct);
        if (rawPaidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }
        e.PaidAmount = rawPaidAmount;
        e.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? e.LastPaymentDate;
        e.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        e.InvoiceId = immutableInvoiceId;
        e.DocumentKind = immutableDocumentKind;
        e.Account = immutableAccount;
        e.AccountDate = immutableAccountDate;
        e.LegacyAccountNumber = immutableLegacyAccountNumber;
        e.LegacyAccountDate = immutableLegacyAccountDate;
        e.Type = immutableType;
        if (e.DocumentKind == DocumentKind.Invoice)
        {
            e.Notes = originalNotes;
        }

        await SyncInvoiceManagedPaymentBindingsAsync(e, ct);
        await PopulateMissingExpenseAttributionFromCaseAsync(e, ct);
        var invoiceManagedValidationError = await ValidatePaymentAttributionAsync(e, ct);
        if (invoiceManagedValidationError is not null) return invoiceManagedValidationError;
        if (appliedCreditAmount > AmountTolerance
            && (e.ClientId != originalClientId || e.LegalEntityId != originalLegalEntityId || e.Type != immutableType))
            return BadRequest(new { message = "Нельзя менять контрагента или направление платежа с зачтённым балансом." });
        if (e.Amount + AmountTolerance < appliedCreditAmount)
            return BadRequest(new { message = "Сумма платежа не может быть меньше зачтённого баланса контрагента." });
        PaymentDomainService.Normalize(e);

        if (e.LastPaymentDate is null && e.PaidAmount > 0m)
        {
            e.LastPaymentDate = (model.LastPaymentDate ?? model.PaidDate)?.Date;
        }

        PaymentDomainService.ApplyStatusRules(e, now, paymentTimeZone);
        SyncInstallmentScheduleMetadataDate(e);

        var rescheduleCount = originalRescheduleCount;
        var plannedDateChanged = model.PlannedDate != default && originalPlannedDate != e.PlannedDate.Date;
        if (plannedDateChanged)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {e.PlannedDate:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        var userChangedDueDate = model.Date.Date != originalDueDate;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {e.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }
        else if (originalDueDate != e.Date.Date)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа автоматически скорректирована на {e.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        if (Math.Abs(originalPaidAmount - e.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {e.PaidAmount.ToString("N2", culture)}",
                now,
                actor,
                paymentTimeZone);
        }

        if (originalStatus != e.Status)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {e.Status.ToDisplayString()}",
                now,
                actor,
                paymentTimeZone);
        }

        e.RescheduleCount = rescheduleCount;

        var overpaymentValidationError = await ValidatePaymentOverpaymentHandlingAsync(e, rawPaidAmount, ct);
        if (overpaymentValidationError is not null) return overpaymentValidationError;

        await ApplyPaymentOverpaymentHandlingAsync(e, rawPaidAmount, now, paymentTimeZone, ct);
        if (e.PaidAmount + AmountTolerance < appliedCreditAmount)
            return BadRequest(new { message = "Оплата не может быть меньше зачтённого баланса контрагента." });
        await SyncPaymentCashOrderAsync(e, now, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, e, now, ct);
        if (await PaymentRemainderWriteRules.ValidateUpdateAsync(_db, originalTransferPayment, e, ct) is { } normalizedTransferError)
            return Conflict(new { message = normalizedTransferError });

        await _db.SaveChangesAsync(ct);
        PopulateAuditMetadata(_db, e);
        return Ok(e);
    }

    [HttpGet("partial-remainder-candidates")]
    public async Task<ActionResult<PartialPaymentRemainderCandidatesResponse>> GetPartialRemainderCandidates(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] PaymentType? type = null,
        [FromQuery] bool includeFuture = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var now = DateTime.UtcNow;
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        var today = PaymentDomainService.ResolveLocalToday(now, paymentTimeZone);
        var sourceIdsWithRemainders = await GetPartialRemainderSourceIdsAsync(ct);
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }

        var query = _db.Payments
            .AsQueryable()
            .ApplyPaymentVisibility(visibility)
            .WithPaymentIncludes()
            .ApplyPaymentFilters(from, to, clientId, caseId, null, clientRole, legalEntityId)
            .Where(payment =>
                payment.DocumentKind != DocumentKind.Invoice
                && payment.Status != PaymentStatus.Cancelled
                && payment.PaidAmount > 0m
                && (payment.Amount > payment.PaidAmount + AmountTolerance
                    || (payment.SystemNotes != null && payment.SystemNotes.Contains(LegacyPartialPaymentNoteText))
                    || (payment.Notes != null && payment.Notes.Contains(LegacyPartialPaymentNoteText))));

        if (!includeFuture)
        {
            query = query.Where(payment => payment.Date < today);
        }

        if (type.HasValue)
        {
            query = query.Where(payment => payment.Type == type.Value);
        }

        var rawCandidates = await query
            .OrderByDescending(payment => payment.Date)
            .ThenByDescending(payment => payment.Id)
            .AsNoTracking()
            .ToListAsync(ct);

        var candidates = new List<PartialPaymentRemainderCandidateDto>();
        foreach (var payment in rawCandidates)
        {
            if (sourceIdsWithRemainders.Contains(payment.Id)
                || PaymentRemainderTransferRules.IsPartialRemainderSourcePayment(payment)
                || !TryBuildPartialRemainderCandidate(payment, today, out var candidate))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        var total = candidates.Count;
        var pageItems = candidates
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Ok(new PartialPaymentRemainderCandidatesResponse
        {
            Items = pageItems,
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalOriginalAmount = candidates.Sum(item => item.OriginalAmount),
            TotalPaidAmount = candidates.Sum(item => item.PaidAmount),
            TotalRemainderAmount = candidates.Sum(item => item.RemainderAmount),
            IncludeFuture = includeFuture,
            CheckedAt = now,
        });
    }

    private static bool TryBuildPartialRemainderCandidate(
        Payment payment,
        DateTime today,
        out PartialPaymentRemainderCandidateDto candidate)
    {
        candidate = default!;

        var paidAmount = Math.Round(payment.PaidAmount, 2, MidpointRounding.AwayFromZero);
        if (paidAmount <= 0m)
        {
            return false;
        }

        var originalAmount = ResolveStoredPartialRemainderOriginalAmount(payment);
        var remainderAmount = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);
        if (remainderAmount <= AmountTolerance)
        {
            return false;
        }

        var suggestedDate = TryInferSuggestedRemainderDate(payment, out var hintedDate)
            ? hintedDate
            : today;

        candidate = new PartialPaymentRemainderCandidateDto
        {
            Payment = payment,
            OriginalAmount = originalAmount,
            PaidAmount = paidAmount,
            RemainderAmount = remainderAmount,
            DaysPastDue = payment.Date.Date < today ? (today - payment.Date.Date).Days : 0,
            SuggestedRemainderDate = suggestedDate,
        };
        return true;
    }

    [HttpPost("{id:int}/partial-remainder/from-existing")]
    public Task<IActionResult> TransferExistingPartialRemainder(
        int id,
        [FromBody] TransferExistingPaymentRemainderRequest request,
        CancellationToken ct)
        => RunRemainderWriteAsync(id, scope => TransferExistingPartialRemainderCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> TransferExistingPartialRemainderCoreAsync(int id,
        TransferExistingPaymentRemainderRequest request, PaymentWriteScope scope, CancellationToken ct)
    {
        if (request.RemainderDate == default)
        {
            return BadRequest(new { message = "Укажите дату переноса остатка." });
        }

        var source = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(payment => payment.Id == id, ct);
        if (source is null) return NotFound();

        var originalAmount = ResolveStoredPartialRemainderOriginalAmount(source);
        var paidAmount = Math.Round(source.PaidAmount, 2, MidpointRounding.AwayFromZero);
        var remainderAmount = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);
        if (paidAmount <= 0m || remainderAmount <= AmountTolerance)
        {
            return BadRequest(new { message = "У платежа нет частично оплаченного остатка для переноса." });
        }

        var transferModel = CreateStoredPaymentTransferModel(source);
        transferModel.Amount = originalAmount;
        _skipRemainderTransferFormRequirements = true;
        try
        {
            return await TransferPartialRemainderCoreAsync(
                id,
                new TransferPaymentRemainderRequest
                {
                    Payment = transferModel,
                    RemainderDate = request.RemainderDate.Date,
                },
                scope,
                ct);
        }
        finally
        {
            _skipRemainderTransferFormRequirements = false;
        }
    }

    [HttpPut("{id:int}/partial-remainder")]
    public Task<IActionResult> TransferPartialRemainder(
        int id,
        [FromBody] TransferPaymentRemainderRequest request,
        CancellationToken ct)
        => RunRemainderWriteAsync(id, scope => TransferPartialRemainderCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> TransferPartialRemainderCoreAsync(int id,
        TransferPaymentRemainderRequest request, PaymentWriteScope scope, CancellationToken ct)
    {
        if (request.Payment is null)
        {
            return BadRequest(new { message = "Не переданы данные платежа." });
        }

        if (request.RemainderDate == default)
        {
            return BadRequest(new { message = "Укажите дату переноса остатка." });
        }

        if (!_skipRemainderTransferFormRequirements)
        {
            var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", request.Payment);
            if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });
        }

        var source = await LoadPaymentForRemainderTransferAsync(id, ct);
        if (source is null) return NotFound();
        if (source.DocumentKind == DocumentKind.Invoice)
        {
            return Conflict(new { message = "Остаток переносится по платежу, а не по самой записи счета." });
        }

        var existingRemainder = await FindPartialRemainderPaymentAsync(source.Id, ct);
        if (existingRemainder is not null)
        {
            var visibility = await ResolvePaymentsVisibilityAsync(ct);
            if (visibility is null || !await CanAccessPaymentAsync(existingRemainder.Id, visibility, ct)) return NotFound();
            await scope.LockPaymentAsync(existingRemainder.Id, ct);
            PopulateAuditMetadata(_db, source);
            PopulateAuditMetadata(_db, existingRemainder);
            return Ok(new TransferPaymentRemainderResponse
            {
                Payment = source,
                RemainderPayment = existingRemainder,
                CreatedRemainder = false,
            });
        }

        if (source.Status == PaymentStatus.Cancelled)
        {
            return BadRequest(new { message = "Нельзя перенести остаток по отмененному платежу." });
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        var model = request.Payment;
        var sourceAmount = Math.Round(source.Amount, 2, MidpointRounding.AwayFromZero);
        var modelAmount = Math.Round(Math.Max(model.Amount, 0m), 2, MidpointRounding.AwayFromZero);
        var originalAmount = _skipRemainderTransferFormRequirements && modelAmount > sourceAmount + AmountTolerance
            ? modelAmount
            : sourceAmount;
        var paidAmount = Math.Round(Math.Max(model.PaidAmount, 0m), 2, MidpointRounding.AwayFromZero);
        var remainderAmount = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);

        if (paidAmount <= 0m)
        {
            return BadRequest(new { message = "Для переноса остатка укажите фактически оплаченную сумму." });
        }

        if (remainderAmount <= AmountTolerance)
        {
            return BadRequest(new { message = "Остаток к переносу не образуется." });
        }

        var invoiceManaged = InvoiceManagedPaymentRules.IsInvoiceManagedPayment(source);
        var targetType = invoiceManaged ? source.Type : model.Type;
        if (model.IncomeTypeId.HasValue)
        {
            var incomeType = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value, ct);
            if (incomeType is null) return BadRequest("Unknown IncomeTypeId");
            if (incomeType.PaymentType != targetType) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, source.Id, ct);
        if (paidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }


        var originalDueDate = source.Date.Date;
        var originalPlannedDate = (source.PlannedDate == default ? source.Date : source.PlannedDate).Date;
        var originalPaidAmount = source.PaidAmount;
        var originalStatus = source.Status;
        var originalRescheduleCount = Math.Max(0, source.RescheduleCount);
        var originalSystemNotes = source.SystemNotes ?? string.Empty;
        if (PaymentCreateInputRules.ValidateSystemNotesChange(originalSystemNotes, model.SystemNotes) is { } notesError)
            return BadRequest(new { message = notesError });
        var originalInvoiceId = source.InvoiceId;
        var originalDescription = source.Description;
        var originalNotes = source.Notes;
        var originalClientId = source.ClientId;
        var originalLegalEntityId = source.LegalEntityId;
        var originalType = source.Type;

        if (source.PlannedDate == default)
        {
            source.PlannedDate = originalPlannedDate;
        }

        var immutableInvoiceId = source.InvoiceId;
        var immutableDocumentKind = source.DocumentKind;
        var immutableAccount = source.Account;
        var immutableAccountDate = source.AccountDate;
        var immutableLegacyAccountNumber = source.LegacyAccountNumber;
        var immutableLegacyAccountDate = source.LegacyAccountDate;
        var immutableType = source.Type;

        source.Date = model.Date == default ? source.Date : model.Date;
        source.PlannedDate = model.PlannedDate == default ? source.PlannedDate : model.PlannedDate;
        source.Amount = paidAmount;
        source.Type = targetType;
        source.Status = PaymentStatus.Completed;
        source.Description = model.Description ?? string.Empty;
        source.Notes = model.Notes ?? string.Empty;
        source.ClientId = model.ClientId;
        source.LegalEntityId = model.LegalEntityId;
        source.ExpenseClientId = model.ExpenseClientId;
        source.ExpenseLegalEntityId = model.ExpenseLegalEntityId;
        source.ClientCaseId = model.ClientCaseId;
        source.ContractId = model.ContractId;
        source.DealTypeId = model.DealTypeId;
        source.IncomeTypeId = model.IncomeTypeId;
        source.ServiceId = model.ServiceId;
        source.PaymentSourceId = model.PaymentSourceId;
        source.PaymentStatusId = model.PaymentStatusId;
        source.InvoiceId = model.InvoiceId;
        source.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        source.DocumentKind = model.DocumentKind == default ? source.DocumentKind : model.DocumentKind;
        source.AccountDate = model.AccountDate;
        source.PaidAmount = paidAmount;
        source.PaidDate = model.PaidDate ?? model.LastPaymentDate ?? source.PaidDate ?? source.Date;
        source.LastPaymentDate = model.LastPaymentDate ?? source.PaidDate ?? source.LastPaymentDate;
        source.PaymentOrderNumber = model.PaymentOrderNumber;
        source.PaymentDocumentDate = model.PaymentDocumentDate;
        source.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        if (invoiceManaged)
        {
            source.InvoiceId = immutableInvoiceId;
            source.DocumentKind = immutableDocumentKind;
            source.Account = immutableAccount;
            source.AccountDate = immutableAccountDate;
            source.LegacyAccountNumber = immutableLegacyAccountNumber;
            source.LegacyAccountDate = immutableLegacyAccountDate;
            source.Type = immutableType;
            if (source.DocumentKind == DocumentKind.Invoice)
            {
                source.Description = originalDescription;
                source.Notes = originalNotes;
            }

            await SyncInvoiceManagedPaymentBindingsAsync(source, ct);
        }
        else
        {
            await SyncInvoiceLinkAsync(source, ct);
        }

        if (await ValidateInvoiceLinkAccessAsync(source, ct) is { } invalidInvoice) return invalidInvoice;
        if (appliedCreditAmount > AmountTolerance
            && (source.ClientId != originalClientId || source.LegalEntityId != originalLegalEntityId || source.Type != originalType))
            return BadRequest(new { message = "Нельзя менять контрагента или направление платежа с зачтённым балансом." });
        var sourceValidationError = await ValidatePaymentAttributionAsync(source, ct);
        if (sourceValidationError is not null) return sourceValidationError;
        PaymentDomainService.Normalize(source);
        PaymentDomainService.ApplyStatusRules(source, now, paymentTimeZone);

        AppendSourceRemainderAuditNotes(
            source,
            originalAmount,
            paidAmount,
            remainderAmount,
            request.RemainderDate.Date,
            now,
            actor,
            paymentTimeZone);
        source.SystemNotes = ReplaceTechnicalMarkerLine(
            source.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderSourceMarker,
            PaymentRemainderTransferRules.BuildSourceMarker(
                source.Id,
                originalAmount,
                paidAmount,
                remainderAmount,
                request.RemainderDate.Date));

        if (model.PlannedDate != default && originalPlannedDate != source.PlannedDate.Date)
        {
            PaymentDomainService.AppendSystemNote(
                source,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {source.PlannedDate:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        var userChangedDueDate = model.Date != default && model.Date.Date != originalDueDate;
        var rescheduleCount = originalRescheduleCount;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                source,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {source.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        if (Math.Abs(originalPaidAmount - source.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                source,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {source.PaidAmount.ToString("N2", culture)}",
                now,
                actor,
                paymentTimeZone);
        }

        if (originalStatus != source.Status)
        {
            PaymentDomainService.AppendSystemNote(
                source,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {source.Status.ToDisplayString()}",
                now,
                actor,
                paymentTimeZone);
        }

        source.RescheduleCount = rescheduleCount;

        var remainderPayment = CreateRemainderPaymentFromSource(
            source,
            originalAmount,
            paidAmount,
            remainderAmount,
            request.RemainderDate.Date,
            now,
            paymentTimeZone);
        await CopyPaymentResponsiblesAsync(remainderPayment, source.Responsibles.Select(item => item.UserId), ct);
        CopyPaymentInvoiceClientCases(remainderPayment, source.InvoiceClientCases.Select(item => item.ClientCaseId));

        PaymentDomainService.Normalize(remainderPayment);
        PaymentDomainService.ApplyStatusRules(remainderPayment, now, paymentTimeZone);

        var remainderValidationError = await ValidatePaymentAttributionAsync(remainderPayment, ct);
        if (remainderValidationError is not null) return remainderValidationError;

        _db.Payments.Add(remainderPayment);

        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, source, paidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, source, now, ct);
        if (originalInvoiceId.HasValue
            && originalInvoiceId.Value > 0
            && originalInvoiceId != source.InvoiceId)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, now, ct);
        }

        await _db.SaveChangesAsync(ct);
        UpsertPartialRemainderTransfer(
            source.Id,
            remainderPayment.Id,
            originalAmount,
            paidAmount,
            remainderAmount,
            request.RemainderDate.Date,
            now);
        await _db.SaveChangesAsync(ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, remainderPayment, now, ct);
        await _db.SaveChangesAsync(ct);

        PopulateAuditMetadata(_db, source);
        PopulateAuditMetadata(_db, remainderPayment);
        return Ok(new TransferPaymentRemainderResponse
        {
            Payment = source,
            RemainderPayment = remainderPayment,
            CreatedRemainder = true,
        });
    }

    [HttpPost("{id:int}/partial-remainder/rollback")]
    public Task<IActionResult> RollbackPartialRemainderTransfer(int id, CancellationToken ct)
        => RunRemainderWriteAsync(id, scope => RollbackPartialRemainderTransferCoreAsync(id, scope, ct), ct);

    private async Task<IActionResult> RollbackPartialRemainderTransferCoreAsync(int id, PaymentWriteScope scope, CancellationToken ct)
    {
        var pair = await ResolvePartialRemainderTransferPairAsync(id, ct);
        if (pair is null)
        {
            return BadRequest(new { message = "Перенос остатка для платежа не найден." });
        }

        if (await LockRemainderPairAsync(id, pair.Value, scope, ct) is { } inaccessible) return inaccessible;
        var validationError = await ValidateRemainderCanBeReworkedAsync(pair.Value, ct);
        if (validationError is not null) return validationError;

        var now = DateTime.UtcNow;
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var source = pair.Value.Source;
        var remainder = pair.Value.Remainder;
        var marker = pair.Value.Marker;

        if (marker.PaidAmount + AmountTolerance < await ClientBalanceService.GetAppliedCreditAmountAsync(_db, source.Id, ct))
            return BadRequest(new { message = "Оплата не может быть меньше зачтённого баланса контрагента." });

        source.Amount = marker.SourceOriginalAmount;
        source.PaidAmount = marker.PaidAmount;
        source.IsPaid = false;
        source.PaidDate = null;
        source.SystemNotes = RemoveTechnicalMarkerLines(
            source.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderSourceMarker);
        PaymentDomainService.AppendSystemNote(
            source,
            $"Перенос остатка отменен: платеж #{remainder.Id} на {marker.RemainderAmount.ToString("N2", culture)} удален, исходная сумма восстановлена до {marker.SourceOriginalAmount.ToString("N2", culture)}.",
            now,
            actor,
            paymentTimeZone);
        PaymentDomainService.Normalize(source);
        PaymentDomainService.ApplyStatusRules(source, now, paymentTimeZone);

        await RemovePartialRemainderTransferAsync(source.Id, remainder.Id, ct);
        _db.Payments.Remove(remainder);
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, source, source.PaidAmount, now, HttpContext, ct);
        await _db.SaveChangesAsync(ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, source, now, ct);
        await _db.SaveChangesAsync(ct);

        PopulateAuditMetadata(_db, source);
        return Ok(new RollbackPaymentRemainderTransferResponse
        {
            Payment = source,
            RemovedRemainderPaymentId = remainder.Id,
        });
    }

    [HttpPut("{id:int}/partial-remainder/adjust")]
    public Task<IActionResult> AdjustPartialRemainderTransfer(
        int id,
        [FromBody] AdjustPaymentRemainderTransferRequest request,
        CancellationToken ct)
        => RunRemainderWriteAsync(id, scope => AdjustPartialRemainderTransferCoreAsync(id, request, scope, ct), ct);

    private async Task<IActionResult> AdjustPartialRemainderTransferCoreAsync(int id,
        AdjustPaymentRemainderTransferRequest request, PaymentWriteScope scope, CancellationToken ct)
    {
        if (request.RemainderDate == default)
        {
            return BadRequest(new { message = "Укажите дату переноса остатка." });
        }

        var pair = await ResolvePartialRemainderTransferPairAsync(id, ct);
        if (pair is null)
        {
            return BadRequest(new { message = "Перенос остатка для платежа не найден." });
        }

        if (await LockRemainderPairAsync(id, pair.Value, scope, ct) is { } inaccessible) return inaccessible;
        var validationError = await ValidateRemainderCanBeReworkedAsync(pair.Value, ct);
        if (validationError is not null) return validationError;

        var now = DateTime.UtcNow;
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var source = pair.Value.Source;
        var remainder = pair.Value.Remainder;
        var marker = pair.Value.Marker;
        var paidAmount = Math.Round(Math.Max(request.PaidAmount, 0m), 2, MidpointRounding.AwayFromZero);
        var sourceOriginalAmount = marker.SourceOriginalAmount;
        var remainderAmount = Math.Round(sourceOriginalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);

        if (paidAmount <= 0m)
        {
            return BadRequest(new { message = "Укажите фактически оплаченную сумму." });
        }

        if (remainderAmount <= 0.009m)
        {
            return BadRequest(new { message = "Остаток к переносу не образуется." });
        }

        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, source.Id, ct);
        if (paidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }


        var remainderDate = request.RemainderDate.Date;
        source.Amount = paidAmount;
        source.PaidAmount = paidAmount;
        source.IsPaid = true;
        source.Status = PaymentStatus.Completed;
        source.PaidDate ??= source.LastPaymentDate ?? source.Date;
        source.LastPaymentDate ??= source.PaidDate;
        source.SystemNotes = ReplaceTechnicalMarkerLine(
            source.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderSourceMarker,
            PaymentRemainderTransferRules.BuildSourceMarker(
                source.Id,
                sourceOriginalAmount,
                paidAmount,
                remainderAmount,
                remainderDate));
        PaymentDomainService.AppendSystemNote(
            source,
            $"Разделение платежа изменено: оплачено {paidAmount.ToString("N2", culture)} / {sourceOriginalAmount.ToString("N2", culture)}, остаток {remainderAmount.ToString("N2", culture)} перенесен на {remainderDate:dd.MM.yyyy}.",
            now,
            actor,
            paymentTimeZone);

        remainder.Amount = remainderAmount;
        remainder.PaidAmount = 0m;
        remainder.IsPaid = false;
        remainder.PaidDate = null;
        remainder.LastPaymentDate = null;
        remainder.Date = remainderDate;
        remainder.PlannedDate = remainderDate;
        var localToday = PaymentDomainService.ResolveLocalToday(now, paymentTimeZone);
        remainder.Status = remainderDate < localToday ? PaymentStatus.Overdue : PaymentStatus.Pending;
        remainder.SystemNotes = ReplaceTechnicalMarkerLine(
            remainder.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderMarker,
            PaymentRemainderTransferRules.BuildMarker(
                source.Id,
                sourceOriginalAmount,
                paidAmount,
                remainderAmount,
                remainderDate));
        PaymentDomainService.AppendSystemNote(
            remainder,
            $"Параметры перенесенного остатка изменены: сумма {remainderAmount.ToString("N2", culture)}, дата {remainderDate:dd.MM.yyyy}.",
            now,
            actor,
            paymentTimeZone);

        PaymentDomainService.Normalize(source);
        PaymentDomainService.ApplyStatusRules(source, now, paymentTimeZone);
        PaymentDomainService.Normalize(remainder);
        PaymentDomainService.ApplyStatusRules(remainder, now, paymentTimeZone);

        var sourceValidationError = await ValidatePaymentAttributionAsync(source, ct);
        if (sourceValidationError is not null) return sourceValidationError;
        var remainderValidationError = await ValidatePaymentAttributionAsync(remainder, ct);
        if (remainderValidationError is not null) return remainderValidationError;

        UpsertPartialRemainderTransfer(
            source.Id,
            remainder.Id,
            sourceOriginalAmount,
            paidAmount,
            remainderAmount,
            remainderDate,
            now);
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, source, paidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, source, now, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, remainder, now, ct);
        await _db.SaveChangesAsync(ct);

        PopulateAuditMetadata(_db, source);
        PopulateAuditMetadata(_db, remainder);
        return Ok(new TransferPaymentRemainderResponse
        {
            Payment = source,
            RemainderPayment = remainder,
            CreatedRemainder = false,
        });
    }

    private async Task<HashSet<int>> GetPartialRemainderSourceIdsAsync(CancellationToken ct)
    {
        var sourceIds = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .Select(item => item.SourcePaymentId)
            .ToHashSetAsync(ct);

        var markerLines = await _db.Payments
            .AsNoTracking()
            .Where(payment => payment.SystemNotes != null
                              && payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker))
            .Select(payment => payment.SystemNotes)
            .ToListAsync(ct);

        foreach (var systemNotes in markerLines)
        {
            if (PaymentRemainderTransferRules.TryReadMarker(systemNotes, out var marker)
                && marker.SourcePaymentId > 0)
            {
                sourceIds.Add(marker.SourcePaymentId);
            }
        }

        return sourceIds;
    }

    private static decimal ResolveStoredPartialRemainderOriginalAmount(Payment payment)
    {
        var storedAmount = Math.Round(payment.Amount, 2, MidpointRounding.AwayFromZero);
        var paidAmount = Math.Round(payment.PaidAmount, 2, MidpointRounding.AwayFromZero);

        if (TryReadLegacyPartialRemainderHint(payment, out var hint)
            && storedAmount <= paidAmount + AmountTolerance
            && hint.OriginalAmount > storedAmount + AmountTolerance
            && hint.OriginalAmount > paidAmount + AmountTolerance)
        {
            return hint.OriginalAmount;
        }

        return storedAmount;
    }

    private static bool TryReadLegacyPartialRemainderHint(Payment payment, out LegacyPartialRemainderHint hint)
    {
        foreach (var text in new[] { payment.SystemNotes, payment.Notes })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var matches = LegacyPartialRemainderNoteRegex.Matches(text);
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var match = matches[index];
                if (!TryParseLegacyMoney(match.Groups["paid"].Value, out var paidAmount)
                    || !TryParseLegacyMoney(match.Groups["original"].Value, out var originalAmount)
                    || !TryParseLegacyMoney(match.Groups["remainder"].Value, out var remainderAmount)
                    || !DateTime.TryParseExact(
                        match.Groups["date"].Value,
                        "dd.MM.yyyy",
                        CultureInfo.GetCultureInfo("ru-RU"),
                        DateTimeStyles.None,
                        out var remainderDate))
                {
                    continue;
                }

                paidAmount = Math.Round(paidAmount, 2, MidpointRounding.AwayFromZero);
                originalAmount = Math.Round(originalAmount, 2, MidpointRounding.AwayFromZero);
                remainderAmount = Math.Round(remainderAmount, 2, MidpointRounding.AwayFromZero);
                var calculatedRemainder = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);
                if (paidAmount <= 0m
                    || originalAmount <= paidAmount + AmountTolerance
                    || remainderAmount <= AmountTolerance
                    || Math.Abs(calculatedRemainder - remainderAmount) > 0.01m)
                {
                    continue;
                }

                hint = new LegacyPartialRemainderHint(
                    originalAmount,
                    paidAmount,
                    remainderAmount,
                    remainderDate.Date);
                return true;
            }
        }

        hint = default;
        return false;
    }

    private static bool TryParseLegacyMoney(string rawValue, out decimal amount)
    {
        var normalized = Regex.Replace(rawValue.Trim(), @"[\s\u00A0\u202F]+", string.Empty)
            .Replace(',', '.');

        return decimal.TryParse(
            normalized,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out amount);
    }

    private async Task<IActionResult?> ValidatePaymentOverpaymentHandlingAsync(
        Payment payment,
        decimal rawPaidAmount,
        CancellationToken ct)
    {
        var overpaymentAmount = CalculateOverpaymentAmount(payment, rawPaidAmount);
        if (overpaymentAmount <= AmountTolerance || payment.Type != PaymentType.Income)
        {
            return null;
        }

        if (!payment.OverpaymentHandling.HasValue)
        {
            return Conflict(new
            {
                code = "payment_overpayment_decision_required",
                message = "Обнаружена переплата. Выберите сценарий обработки переплаты перед сохранением платежа.",
                paymentAmount = payment.Amount,
                enteredAmount = Math.Round(rawPaidAmount, 2, MidpointRounding.AwayFromZero),
                overpaymentAmount,
            });
        }

        if (payment.OverpaymentHandling == PaymentOverpaymentHandling.Advance
            && !HasSingleCounterpartyOwner(payment))
        {
            return BadRequest(new
            {
                message = "Для записи переплаты на баланс у платежа должен быть указан один контрагент."
            });
        }

        if (payment.OverpaymentHandling != PaymentOverpaymentHandling.NextInstallment)
        {
            return null;
        }

        var targetInstallment = await FindOverpaymentTargetInstallmentPaymentAsync(payment, track: false, ct);
        if (targetInstallment is null)
        {
            return BadRequest(new
            {
                message = "Следующий платеж рассрочки для зачета переплаты не найден."
            });
        }

        var nextOutstanding = InvoicePaymentStateService.ResolveOutstandingAmount(targetInstallment);
        if (overpaymentAmount > nextOutstanding + AmountTolerance)
        {
            return BadRequest(new
            {
                message = $"Переплата больше остатка следующего платежа рассрочки. Остаток: {nextOutstanding.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}."
            });
        }

        return null;
    }

    private async Task ApplyPaymentOverpaymentHandlingAsync(
        Payment payment,
        decimal rawPaidAmount,
        DateTime now,
        TimeZoneInfo paymentTimeZone,
        CancellationToken ct)
    {
        var overpaymentAmount = CalculateOverpaymentAmount(payment, rawPaidAmount);
        if (overpaymentAmount <= AmountTolerance || payment.Type != PaymentType.Income)
        {
            await RemovePaymentOverpaymentRefundOperationAsync(payment.Id, ct);
            await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, payment, rawPaidAmount, now, HttpContext, ct);
            return;
        }

        var handling = payment.OverpaymentHandling ?? PaymentOverpaymentHandling.Advance;
        switch (handling)
        {
            case PaymentOverpaymentHandling.NextInstallment:
                await RemovePaymentOverpaymentRefundOperationAsync(payment.Id, ct);
                await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, payment, payment.Amount, now, HttpContext, ct);
                await ApplyOverpaymentToNextInstallmentAsync(payment, overpaymentAmount, now, paymentTimeZone, ct);
                break;

            case PaymentOverpaymentHandling.Refund:
                await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, payment, payment.Amount, now, HttpContext, ct);
                await UpsertPaymentOverpaymentRefundOperationAsync(payment, overpaymentAmount, now, paymentTimeZone, ct);
                break;

            case PaymentOverpaymentHandling.Advance:
            default:
                await RemovePaymentOverpaymentRefundOperationAsync(payment.Id, ct);
                await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, payment, rawPaidAmount, now, HttpContext, ct);
                AppendOverpaymentDecisionNote(
                    payment,
                    $"Переплата {FormatMoney(overpaymentAmount)} записана на баланс контрагента.",
                    now,
                    paymentTimeZone);
                break;
        }
    }

    private async Task ApplyOverpaymentToNextInstallmentAsync(
        Payment payment,
        decimal overpaymentAmount,
        DateTime now,
        TimeZoneInfo paymentTimeZone,
        CancellationToken ct)
    {
        var nextInstallment = await FindOverpaymentTargetInstallmentPaymentAsync(payment, track: true, ct);
        if (nextInstallment is null)
        {
            return;
        }

        nextInstallment.PaidAmount = Math.Round(
            Math.Min(nextInstallment.Amount, Math.Max(nextInstallment.PaidAmount, 0m) + overpaymentAmount),
            2,
            MidpointRounding.AwayFromZero);
        nextInstallment.LastPaymentDate = payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date;
        nextInstallment.PaymentDocumentDate ??= payment.PaymentDocumentDate ?? payment.PaidDate ?? payment.LastPaymentDate ?? payment.Date;
        nextInstallment.PaymentOrderNumber ??= payment.PaymentOrderNumber;
        if (nextInstallment.PaidAmount > AmountTolerance
            && nextInstallment.PaidAmount + AmountTolerance < nextInstallment.Amount
            && nextInstallment.Status != PaymentStatus.Cancelled)
        {
            nextInstallment.Status = PaymentStatus.Processing;
        }

        PaymentDomainService.ApplyStatusRules(nextInstallment, now, paymentTimeZone);

        AppendOverpaymentDecisionNote(
            payment,
            $"Переплата {FormatMoney(overpaymentAmount)} зачтена в следующий платеж рассрочки #{nextInstallment.Id}.",
            now,
            paymentTimeZone);
        AppendOverpaymentDecisionNote(
            nextInstallment,
            $"Зачтена переплата {FormatMoney(overpaymentAmount)} из платежа #{payment.Id}.",
            now,
            paymentTimeZone);
    }

    private async Task<Payment?> FindOverpaymentTargetInstallmentPaymentAsync(
        Payment payment,
        bool track,
        CancellationToken ct)
    {
        if (payment.OverpaymentTargetPaymentId.HasValue)
        {
            return await FindInstallmentPaymentByIdAsync(payment, payment.OverpaymentTargetPaymentId.Value, track, ct);
        }

        return await FindNextInstallmentPaymentAsync(payment, track, ct);
    }

    private async Task<Payment?> FindInstallmentPaymentByIdAsync(
        Payment payment,
        int targetPaymentId,
        bool track,
        CancellationToken ct)
    {
        if (!payment.InvoiceId.HasValue || targetPaymentId == payment.Id)
        {
            return null;
        }

        var query = _db.Payments
            .Where(item =>
                item.InvoiceId == payment.InvoiceId.Value &&
                item.Type == PaymentType.Income &&
                item.Status != PaymentStatus.Cancelled);
        if (!track)
        {
            query = query.AsNoTracking();
        }

        var candidates = await query.ToListAsync(ct);
        var target = candidates.FirstOrDefault(item => item.Id == targetPaymentId);
        if (target is null
            || !IsOverpaymentInstallmentCandidate(target, candidates)
            || InvoicePaymentStateService.ResolveOutstandingAmount(target) <= AmountTolerance)
        {
            return null;
        }

        return target;
    }

    private async Task<Payment?> FindNextInstallmentPaymentAsync(
        Payment payment,
        bool track,
        CancellationToken ct)
    {
        if (!payment.InvoiceId.HasValue)
        {
            return null;
        }

        var query = _db.Payments
            .Where(item =>
                item.InvoiceId == payment.InvoiceId.Value &&
                item.Type == PaymentType.Income &&
                item.Status != PaymentStatus.Cancelled);
        if (!track)
        {
            query = query.AsNoTracking();
        }

        var candidates = await query.ToListAsync(ct);
        var currentDate = InvoicePaymentStateService.EffectivePlannedDate(payment);

        return candidates
            .Where(item => item.Id != payment.Id)
            .Where(item => IsOverpaymentInstallmentCandidate(item, candidates))
            .Where(item => InvoicePaymentStateService.ResolveOutstandingAmount(item) > AmountTolerance)
            .OrderBy(InvoicePaymentStateService.EffectivePlannedDate)
            .ThenBy(item => item.Id)
            .FirstOrDefault(item =>
                InvoicePaymentStateService.EffectivePlannedDate(item) > currentDate ||
                (InvoicePaymentStateService.EffectivePlannedDate(item) == currentDate && item.Id > payment.Id));
    }

    private static bool IsOverpaymentInstallmentCandidate(
        Payment payment,
        IReadOnlyCollection<Payment>? invoicePayments = null)
    {
        if (InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment))
        {
            return true;
        }

        if (payment.InvoiceId.HasValue
            && !string.IsNullOrWhiteSpace(payment.Description)
            && LegacyInstallmentDescriptionRegex.IsMatch(payment.Description))
        {
            return true;
        }

        return IsLegacyInstallmentSibling(payment, invoicePayments);
    }

    private static bool IsLegacyInstallmentSibling(
        Payment payment,
        IReadOnlyCollection<Payment>? invoicePayments)
    {
        if (invoicePayments is null
            || invoicePayments.Count < 3
            || invoicePayments.Any(InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment))
        {
            return false;
        }

        var representativeInvoice = invoicePayments
            .Where(item => item.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(item => item.Amount)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (representativeInvoice is null || representativeInvoice.Id == payment.Id)
        {
            return false;
        }

        var maxSiblingAmount = invoicePayments
            .Where(item => item.Id != representativeInvoice.Id)
            .Select(item => item.Amount)
            .DefaultIfEmpty(0m)
            .Max();

        return representativeInvoice.Amount > maxSiblingAmount + AmountTolerance
               && payment.Amount <= maxSiblingAmount + AmountTolerance;
    }

    private async Task UpsertPaymentOverpaymentRefundOperationAsync(
        Payment payment,
        decimal overpaymentAmount,
        DateTime now,
        TimeZoneInfo paymentTimeZone,
        CancellationToken ct)
    {
        var key = BuildPaymentOverpaymentRefundKey(payment.Id);
        var paymentSourceName = await ResolvePaymentSourceNameAsync(payment, ct);
        var source = ResolveMoneyOperationSource(paymentSourceName);
        var refundDate = (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;
        var invoiceNumber = string.IsNullOrWhiteSpace(payment.Account)
            ? payment.Invoice?.Number
            : payment.Account;
        var counterpartyName = await ResolvePaymentCounterpartyNameAsync(payment, ct);
        var comment = NormalizeOverpaymentComment(payment.OverpaymentComment);
        var purpose = $"Возврат переплаты по платежу #{payment.Id}";
        if (!string.IsNullOrWhiteSpace(invoiceNumber))
        {
            purpose += $" по счету №{invoiceNumber}";
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            purpose += $". Комментарий: {comment}";
        }

        var existing = await _db.MoneyOperations
            .Include(operation => operation.CashOrders)
            .FirstOrDefaultAsync(operation =>
                operation.MatchComment != null &&
                operation.MatchComment.Contains(key), ct);

        if (existing is null)
        {
            existing = new MoneyOperation
            {
                CreatedAt = now,
                MatchStatus = MoneyOperationMatchStatus.Matched,
                MatchedAt = now,
                MatchComment = $"Возврат переплаты по платежу #{payment.Id}. {key}",
            };
            _db.MoneyOperations.Add(existing);
        }

        existing.Source = source;
        existing.Direction = MoneyOperationDirection.Expense;
        existing.AccountingType = MoneyOperationAccountingType.Fact;
        existing.AccountName = paymentSourceName;
        existing.CounterpartyName = counterpartyName;
        existing.CategoryName = "Возврат клиенту";
        existing.Amount = overpaymentAmount;
        existing.Currency = "RUB";
        existing.Date = refundDate;
        existing.RecognitionDate = refundDate;
        existing.Purpose = purpose;
        existing.PaidInvoiceNumber = invoiceNumber;
        existing.PaidAmount = overpaymentAmount;
        existing.PaidAccountName = invoiceNumber;
        existing.LinkedInvoiceId = payment.InvoiceId;
        existing.UpdatedAt = existing.Id > 0 ? now : null;

        await _db.SaveChangesAsync(ct);
        await SyncRefundCashOrderAsync(existing, payment, source, refundDate, overpaymentAmount, purpose, comment, now, ct);

        AppendOverpaymentDecisionNote(
            payment,
            $"Переплата {FormatMoney(overpaymentAmount)} оформлена к возврату клиенту.",
            now,
            paymentTimeZone);
    }

    private async Task UpsertUnallocatedAdvanceRefundOperationAsync(
        Payment payment,
        decimal refundAmount,
        string reason,
        DateTime now,
        CancellationToken ct)
    {
        var key = BuildUnallocatedAdvanceRefundMoneyOperationKey(payment.Id);
        var paymentSourceName = await ResolvePaymentSourceNameAsync(payment, ct);
        var source = ResolveMoneyOperationSource(paymentSourceName);
        var refundDate = (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;
        var counterpartyName = await ResolvePaymentCounterpartyNameAsync(payment, ct);
        var purpose = $"Возврат нераспределенного аванса по платежу #{payment.Id}. Причина: {reason}";

        var existing = await _db.MoneyOperations
            .Include(operation => operation.CashOrders)
            .FirstOrDefaultAsync(operation =>
                operation.MatchComment != null &&
                operation.MatchComment.Contains(key), ct);

        if (existing is null)
        {
            existing = new MoneyOperation
            {
                CreatedAt = now,
                MatchStatus = MoneyOperationMatchStatus.Matched,
                MatchedAt = now,
                MatchComment = $"Возврат нераспределенного аванса по платежу #{payment.Id}. {key}",
            };
            _db.MoneyOperations.Add(existing);
        }

        existing.Source = source;
        existing.Direction = MoneyOperationDirection.Expense;
        existing.AccountingType = MoneyOperationAccountingType.Fact;
        existing.AccountName = paymentSourceName;
        existing.CounterpartyName = counterpartyName;
        existing.CategoryName = "Возврат клиенту";
        existing.Amount = refundAmount;
        existing.Currency = "RUB";
        existing.Date = refundDate;
        existing.RecognitionDate = refundDate;
        existing.Purpose = purpose;
        existing.PaidInvoiceNumber = payment.Account;
        existing.PaidAmount = refundAmount;
        existing.PaidAccountName = payment.Account;
        existing.LinkedInvoiceId = payment.InvoiceId;
        existing.UpdatedAt = existing.Id > 0 ? now : null;

        await _db.SaveChangesAsync(ct);
        await SyncRefundCashOrderAsync(existing, payment, source, refundDate, refundAmount, purpose, reason, now, ct);
    }

    private async Task SyncRefundCashOrderAsync(
        MoneyOperation operation,
        Payment payment,
        MoneyOperationSource source,
        DateTime refundDate,
        decimal overpaymentAmount,
        string purpose,
        string? comment,
        DateTime now,
        CancellationToken ct)
    {
        var existingOrders = await _db.CashOrders
            .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
            .ToListAsync(ct);

        if (source != MoneyOperationSource.Cash)
        {
            foreach (var order in existingOrders)
            {
                order.Status = CashOrderStatus.Cancelled;
                order.CancelledAt = now;
                order.CancelledReason = "Возврат переплаты перенесен из кассы в безналичный источник.";
            }

            return;
        }

        var orderToUpdate = existingOrders.FirstOrDefault();
        if (orderToUpdate is null)
        {
            _db.CashOrders.Add(new CashOrder
            {
                Number = await GenerateCashOrderNumberAsync(CashOrderType.Outgoing, refundDate, ct),
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = refundDate,
                Amount = overpaymentAmount,
                Currency = "RUB",
                ClientId = payment.ClientId,
                PaymentId = payment.Id,
                MoneyOperationId = operation.Id,
                InvoiceId = payment.InvoiceId,
                Account = operation.AccountName,
                ReceiverName = operation.CounterpartyName,
                Basis = purpose,
                Comment = comment,
                CreatedAt = now,
            });
            return;
        }

        orderToUpdate.Type = CashOrderType.Outgoing;
        orderToUpdate.Status = CashOrderStatus.Posted;
        orderToUpdate.Date = refundDate;
        orderToUpdate.Amount = overpaymentAmount;
        orderToUpdate.Currency = "RUB";
        orderToUpdate.ClientId = payment.ClientId;
        orderToUpdate.PaymentId = payment.Id;
        orderToUpdate.InvoiceId = payment.InvoiceId;
        orderToUpdate.Account = operation.AccountName;
        orderToUpdate.ReceiverName = operation.CounterpartyName;
        orderToUpdate.Basis = purpose;
        orderToUpdate.Comment = comment;
        orderToUpdate.UpdatedAt = now;
    }

    private async Task SyncPaymentCashOrderAsync(Payment payment, DateTime now, CancellationToken ct)
    {
        if (payment.Id <= 0)
        {
            return;
        }

        var existingOrders = await _db.CashOrders
            .Where(order =>
                order.PaymentId == payment.Id &&
                order.MoneyOperationId == null &&
                order.Status != CashOrderStatus.Cancelled)
            .OrderBy(order => order.Id)
            .ToListAsync(ct);

        var paymentSourceName = await ResolvePaymentSourceNameAsync(payment, ct);
        var isCashPayment = ResolveMoneyOperationSource(paymentSourceName) == MoneyOperationSource.Cash;
        var shouldHaveCashOrder = isCashPayment
            && payment.Status == PaymentStatus.Completed
            && payment.PaidAmount > AmountTolerance;

        if (!shouldHaveCashOrder)
        {
            foreach (var order in existingOrders)
            {
                order.Status = CashOrderStatus.Cancelled;
                order.CancelledAt = now;
                order.CancelledReason = "Платеж больше не является выполненным наличным платежом.";
                order.UpdatedAt = now;
            }

            return;
        }

        var orderType = payment.Type == PaymentType.Expense
            ? CashOrderType.Outgoing
            : CashOrderType.Incoming;
        var orderDate = (payment.PaymentDocumentDate ?? payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;
        var orderToUpdate = existingOrders.FirstOrDefault();
        var orderNumber = await ResolveCashOrderNumberAsync(
            orderType,
            orderDate,
            Normalize(payment.PaymentOrderNumber),
            orderToUpdate?.Number,
            orderToUpdate?.Id,
            ct);

        payment.PaymentOrderNumber = orderNumber;
        payment.PaymentDocumentDate = orderDate;

        foreach (var duplicate in existingOrders.Skip(1))
        {
            duplicate.Status = CashOrderStatus.Cancelled;
            duplicate.CancelledAt = now;
            duplicate.CancelledReason = "Дубль кассового ордера по платежу отменен при синхронизации.";
            duplicate.UpdatedAt = now;
        }

        var counterpartyName = await ResolvePaymentCounterpartyNameAsync(payment, ct);
        var basis = BuildPaymentCashOrderBasis(payment);
        var clientId = payment.Type == PaymentType.Expense
            ? payment.ExpenseClientId
            : payment.ClientId;

        if (orderToUpdate is null)
        {
            _db.CashOrders.Add(new CashOrder
            {
                Number = orderNumber,
                Type = orderType,
                Status = CashOrderStatus.Posted,
                Date = orderDate,
                Amount = payment.PaidAmount,
                Currency = "RUB",
                ClientId = clientId,
                PaymentId = payment.Id,
                InvoiceId = payment.InvoiceId,
                Account = payment.Account,
                PayerName = orderType == CashOrderType.Incoming ? counterpartyName : null,
                ReceiverName = orderType == CashOrderType.Outgoing ? counterpartyName : null,
                Basis = basis,
                Comment = payment.Notes,
                CreatedAt = now,
            });
            return;
        }

        orderToUpdate.Number = orderNumber;
        orderToUpdate.Type = orderType;
        orderToUpdate.Status = CashOrderStatus.Posted;
        orderToUpdate.Date = orderDate;
        orderToUpdate.Amount = payment.PaidAmount;
        orderToUpdate.Currency = "RUB";
        orderToUpdate.ClientId = clientId;
        orderToUpdate.PaymentId = payment.Id;
        orderToUpdate.InvoiceId = payment.InvoiceId;
        orderToUpdate.Account = payment.Account;
        orderToUpdate.PayerName = orderType == CashOrderType.Incoming ? counterpartyName : null;
        orderToUpdate.ReceiverName = orderType == CashOrderType.Outgoing ? counterpartyName : null;
        orderToUpdate.Basis = basis;
        orderToUpdate.Comment = payment.Notes;
        orderToUpdate.UpdatedAt = now;
    }

    private async Task<string> ResolveCashOrderNumberAsync(
        CashOrderType type,
        DateTime date,
        string? requestedNumber,
        string? currentOrderNumber,
        int? currentOrderId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedNumber) &&
            !await IsCashOrderNumberUsedByAnotherOrderAsync(requestedNumber, currentOrderId, ct))
        {
            return requestedNumber;
        }

        var normalizedCurrentNumber = Normalize(currentOrderNumber);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentNumber) &&
            !await IsCashOrderNumberUsedByAnotherOrderAsync(normalizedCurrentNumber, currentOrderId, ct))
        {
            return normalizedCurrentNumber;
        }

        return await GenerateCashOrderNumberAsync(type, date, ct);
    }

    private Task<bool> IsCashOrderNumberUsedByAnotherOrderAsync(
        string number,
        int? currentOrderId,
        CancellationToken ct)
        => _db.CashOrders.AnyAsync(order =>
            order.Number == number &&
            (!currentOrderId.HasValue || order.Id != currentOrderId.Value),
            ct);

    private static string BuildPaymentCashOrderBasis(Payment payment)
    {
        var action = payment.Type == PaymentType.Expense
            ? "Выдача наличных"
            : "Прием наличных";
        var invoicePart = string.IsNullOrWhiteSpace(payment.Account)
            ? null
            : $"счет №{payment.Account.Trim()}";
        var descriptionPart = Normalize(payment.Description);

        return string.Join(". ", new[] { action, invoicePart, descriptionPart }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private async Task RemovePaymentOverpaymentRefundOperationAsync(int paymentId, CancellationToken ct)
    {
        if (paymentId <= 0)
        {
            return;
        }

        var key = BuildPaymentOverpaymentRefundKey(paymentId);
        var existing = await _db.MoneyOperations
            .Include(operation => operation.CashOrders)
            .FirstOrDefaultAsync(operation =>
                operation.MatchComment != null &&
                operation.MatchComment.Contains(key), ct);
        if (existing is null)
        {
            return;
        }

        _db.CashOrders.RemoveRange(existing.CashOrders);
        _db.MoneyOperations.Remove(existing);
    }

    private async Task<string?> ResolvePaymentSourceNameAsync(Payment payment, CancellationToken ct)
    {
        if (payment.PaymentSource is not null)
        {
            return payment.PaymentSource.Name;
        }

        if (!payment.PaymentSourceId.HasValue)
        {
            return null;
        }

        return await _db.PaymentSources
            .AsNoTracking()
            .Where(source => source.Id == payment.PaymentSourceId.Value)
            .Select(source => source.Name)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string?> ResolvePaymentCounterpartyNameAsync(Payment payment, CancellationToken ct)
    {
        if (payment.Type == PaymentType.Expense)
        {
            if (payment.ExpenseClientId.HasValue)
            {
                return await _db.Clients
                    .AsNoTracking()
                    .Where(client => client.Id == payment.ExpenseClientId.Value)
                    .Select(client => client.Name)
                    .FirstOrDefaultAsync(ct);
            }

            if (payment.ExpenseLegalEntityId.HasValue)
            {
                return await _db.LegalEntities
                    .AsNoTracking()
                    .Where(entity => entity.Id == payment.ExpenseLegalEntityId.Value)
                    .Select(entity => entity.ShortName ?? entity.FullName)
                    .FirstOrDefaultAsync(ct);
            }
        }

        if (payment.ClientId.HasValue)
        {
            return await _db.Clients
                .AsNoTracking()
                .Where(client => client.Id == payment.ClientId.Value)
                .Select(client => client.Name)
                .FirstOrDefaultAsync(ct);
        }

        if (payment.LegalEntityId.HasValue)
        {
            return await _db.LegalEntities
                .AsNoTracking()
                .Where(entity => entity.Id == payment.LegalEntityId.Value)
                .Select(entity => entity.ShortName ?? entity.FullName)
                .FirstOrDefaultAsync(ct);
        }

        return payment.CounterpartyName;
    }

    private static MoneyOperationSource ResolveMoneyOperationSource(string? sourceName)
    {
        if (ContainsPaymentSourceToken(sourceName, "кас") ||
            ContainsPaymentSourceToken(sourceName, "налич") ||
            ContainsPaymentSourceToken(sourceName, "cash"))
        {
            return MoneyOperationSource.Cash;
        }

        if (ContainsPaymentSourceToken(sourceName, "карт") ||
            ContainsPaymentSourceToken(sourceName, "card"))
        {
            return MoneyOperationSource.Card;
        }

        if (ContainsPaymentSourceToken(sourceName, "e-money") ||
            ContainsPaymentSourceToken(sourceName, "электрон"))
        {
            return MoneyOperationSource.EMoney;
        }

        return MoneyOperationSource.Bank;
    }

    private static bool ContainsPaymentSourceToken(string? sourceName, string token)
        => !string.IsNullOrWhiteSpace(sourceName) &&
           sourceName.Contains(token, StringComparison.OrdinalIgnoreCase);

    private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct)
    {
        var prefix = type == CashOrderType.Incoming ? "ПКО" : "РКО";
        var yearPrefix = $"{prefix}-{date:yyyy}-";
        var sequence = await _db.CashOrders.CountAsync(order => order.Number.StartsWith(yearPrefix), ct) + 1;

        while (true)
        {
            var number = $"{yearPrefix}{sequence:0000}";
            if (!await _db.CashOrders.AnyAsync(order => order.Number == number, ct))
            {
                return number;
            }

            sequence++;
        }
    }

    private static string BuildPaymentOverpaymentRefundKey(int paymentId) => $"payment-overpayment-refund:{paymentId}";

    private static string BuildUnallocatedAdvanceRefundMoneyOperationKey(int paymentId)
        => $"payment-unallocated-advance-refund-operation:{paymentId}";

    private static decimal CalculateOverpaymentAmount(Payment payment, decimal rawPaidAmount)
        => Math.Round(Math.Max(rawPaidAmount - payment.Amount, 0m), 2, MidpointRounding.AwayFromZero);

    private static bool HasSingleCounterpartyOwner(Payment payment)
        => (payment.ClientId.HasValue && payment.ClientId.Value > 0 && !payment.LegalEntityId.HasValue)
            || (payment.LegalEntityId.HasValue && payment.LegalEntityId.Value > 0 && !payment.ClientId.HasValue);

    private static string? NormalizeOverpaymentComment(string? comment)
        => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

    private static string FormatMoney(decimal amount)
        => amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));

    private void AppendOverpaymentDecisionNote(Payment payment, string message, DateTime now, TimeZoneInfo paymentTimeZone)
    {
        var comment = NormalizeOverpaymentComment(payment.OverpaymentComment);
        PaymentDomainService.AppendSystemNote(
            payment,
            string.IsNullOrWhiteSpace(comment) ? message : $"{message} Комментарий: {comment}",
            now,
            User.GetDisplayName(),
            paymentTimeZone);
    }

    private static bool TryInferSuggestedRemainderDate(Payment payment, out DateTime suggestedDate)
    {
        foreach (var text in new[] { payment.SystemNotes, payment.Notes })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var match = RemainderDateHintRegex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            if (DateTime.TryParseExact(
                    match.Groups[1].Value,
                    "dd.MM.yyyy",
                    CultureInfo.GetCultureInfo("ru-RU"),
                    DateTimeStyles.None,
                    out suggestedDate))
            {
                suggestedDate = suggestedDate.Date;
                return true;
            }
        }

        suggestedDate = default;
        return false;
    }

    private readonly record struct LegacyPartialRemainderHint(
        decimal OriginalAmount,
        decimal PaidAmount,
        decimal RemainderAmount,
        DateTime RemainderDate);

    private static Payment CreateStoredPaymentTransferModel(Payment source)
    {
        var effectivePaidDate = source.LastPaymentDate ?? source.PaidDate ?? source.Date;
        return new Payment
        {
            Id = source.Id,
            Account = source.Account,
            AccountDate = source.AccountDate,
            Amount = source.Amount,
            ClientCaseId = source.ClientCaseId,
            ClientCaseReference = source.ClientCaseReference,
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            ContractId = source.ContractId,
            CounterpartyName = source.CounterpartyName,
            CreatedAt = source.CreatedAt,
            Date = source.Date,
            DealTypeId = source.DealTypeId,
            Description = source.Description,
            DocumentKind = source.DocumentKind,
            ExpenseClientId = source.ExpenseClientId,
            ExpenseLegalEntityId = source.ExpenseLegalEntityId,
            IncomeTypeId = source.IncomeTypeId,
            InvoiceId = source.InvoiceId,
            IsNotForAccounting = source.IsNotForAccounting,
            IsPaid = source.IsPaid,
            LastPaymentDate = effectivePaidDate,
            LegacyAccountDate = source.LegacyAccountDate,
            LegacyAccountNumber = source.LegacyAccountNumber,
            Notes = source.Notes,
            PaidAmount = source.PaidAmount,
            PaidDate = effectivePaidDate,
            PaymentDocumentDate = source.PaymentDocumentDate,
            PaymentOrderNumber = source.PaymentOrderNumber,
            PaymentSourceId = source.PaymentSourceId,
            PaymentStatusId = source.PaymentStatusId,
            PlannedDate = source.PlannedDate == default ? source.Date : source.PlannedDate,
            RescheduleCount = source.RescheduleCount,
            ServiceId = source.ServiceId,
            Status = source.Status,
            SystemNotes = source.SystemNotes,
            Type = source.Type,
        };
    }

    private async Task<Payment?> LoadPaymentForRemainderTransferAsync(int id, CancellationToken ct)
        => await _db.Payments
            .ApplyUnambiguousInvoiceLinks()
            .Include(payment => payment.Responsibles)
            .Include(payment => payment.InvoiceClientCases)
            .FirstOrDefaultAsync(payment => payment.Id == id, ct);

    private async Task<Payment?> FindPartialRemainderPaymentAsync(int sourcePaymentId, CancellationToken ct)
    {
        var structuredRemainderId = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .Where(link => link.SourcePaymentId == sourcePaymentId)
            .Select(link => (int?)link.RemainderPaymentId)
            .FirstOrDefaultAsync(ct);

        if (structuredRemainderId.HasValue)
        {
            return await LoadPaymentForRemainderTransferAsync(structuredRemainderId.Value, ct);
        }

        var legacyCandidates = _db.Payments
            .AsNoTracking()
            .ApplyUnambiguousInvoiceLinks()
            .Where(payment => payment.SystemNotes != null
                              && payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker))
            .OrderBy(payment => payment.Id)
            .Select(payment => new { payment.Id, payment.SystemNotes });

        int? foundId = null;
        await foreach (var candidate in legacyCandidates.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (PaymentRemainderTransferRules.TryReadMarker(candidate.SystemNotes, out var marker)
                && marker.SourcePaymentId == sourcePaymentId)
            {
                foundId = candidate.Id;
                break;
            }
        }
        // Close the streaming reader before loading the single selected graph.
        return foundId.HasValue ? await LoadPaymentForRemainderTransferAsync(foundId.Value, ct) : null;
    }

    private async Task<bool> IsPartialRemainderPaymentAsync(Payment payment, CancellationToken ct)
        => PaymentRemainderTransferRules.IsPartialRemainderPayment(payment)
           || await _db.PaymentRemainderTransfers
               .AsNoTracking()
               .AnyAsync(link => link.RemainderPaymentId == payment.Id, ct);

    private static PaymentRemainderTransferRules.PartialRemainderMarkerData ToMarkerData(PaymentRemainderTransfer link)
        => new(
            link.SourcePaymentId,
            Math.Round(link.SourceOriginalAmount, 2, MidpointRounding.AwayFromZero),
            Math.Round(link.PaidAmount, 2, MidpointRounding.AwayFromZero),
            Math.Round(link.RemainderAmount, 2, MidpointRounding.AwayFromZero),
            link.RemainderDate.Date);

    private void UpsertPartialRemainderTransfer(
        int sourcePaymentId,
        int remainderPaymentId,
        decimal sourceOriginalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate,
        DateTime now)
    {
        var link = _db.PaymentRemainderTransfers.Local.FirstOrDefault(item =>
                       item.SourcePaymentId == sourcePaymentId
                       || item.RemainderPaymentId == remainderPaymentId)
                   ?? _db.PaymentRemainderTransfers.FirstOrDefault(item =>
                       item.SourcePaymentId == sourcePaymentId
                       || item.RemainderPaymentId == remainderPaymentId);

        if (link is null)
        {
            _db.PaymentRemainderTransfers.Add(new PaymentRemainderTransfer
            {
                SourcePaymentId = sourcePaymentId,
                RemainderPaymentId = remainderPaymentId,
                SourceOriginalAmount = sourceOriginalAmount,
                PaidAmount = paidAmount,
                RemainderAmount = remainderAmount,
                RemainderDate = remainderDate.Date,
                CreatedAt = now,
            });
            return;
        }

        link.SourcePaymentId = sourcePaymentId;
        link.RemainderPaymentId = remainderPaymentId;
        link.SourceOriginalAmount = sourceOriginalAmount;
        link.PaidAmount = paidAmount;
        link.RemainderAmount = remainderAmount;
        link.RemainderDate = remainderDate.Date;
        link.UpdatedAt = now;
    }

    private async Task RemovePartialRemainderTransferAsync(int sourcePaymentId, int remainderPaymentId, CancellationToken ct)
    {
        var link = await _db.PaymentRemainderTransfers.FirstOrDefaultAsync(item =>
            item.SourcePaymentId == sourcePaymentId
            || item.RemainderPaymentId == remainderPaymentId, ct);
        if (link is not null)
        {
            _db.PaymentRemainderTransfers.Remove(link);
        }
    }

    private async Task<PartialRemainderTransferPair?> ResolvePartialRemainderTransferPairAsync(int paymentId, CancellationToken ct)
    {
        var current = await LoadPaymentForRemainderTransferAsync(paymentId, ct);
        if (current is null)
        {
            return null;
        }

        var structuredAsRemainder = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.RemainderPaymentId == paymentId, ct);
        if (structuredAsRemainder is not null)
        {
            var source = await LoadPaymentForRemainderTransferAsync(structuredAsRemainder.SourcePaymentId, ct);
            return source is null
                ? null
                : new PartialRemainderTransferPair(source, current, ToMarkerData(structuredAsRemainder), structuredAsRemainder.Id);
        }

        var structuredAsSource = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.SourcePaymentId == paymentId, ct);
        if (structuredAsSource is not null)
        {
            var structuredRemainderPayment = await LoadPaymentForRemainderTransferAsync(structuredAsSource.RemainderPaymentId, ct);
            return structuredRemainderPayment is null
                ? null
                : new PartialRemainderTransferPair(current, structuredRemainderPayment, ToMarkerData(structuredAsSource), structuredAsSource.Id);
        }

        if (PaymentRemainderTransferRules.TryReadMarker(current.SystemNotes, out var currentMarker)
            && currentMarker.SourcePaymentId > 0)
        {
            var source = await LoadPaymentForRemainderTransferAsync(currentMarker.SourcePaymentId, ct);
            return source is null
                ? null
                : new PartialRemainderTransferPair(source, current, currentMarker, null);
        }

        var remainder = await FindPartialRemainderPaymentAsync(current.Id, ct);
        if (remainder is null
            || !PaymentRemainderTransferRules.TryReadMarker(remainder.SystemNotes, out var marker))
        {
            return null;
        }

        return new PartialRemainderTransferPair(current, remainder, marker, null);
    }

    private async Task<ActionResult?> ValidateRemainderCanBeReworkedAsync(
        PartialRemainderTransferPair pair,
        CancellationToken ct)
    {
        if (pair.Remainder.IsPaid
            || pair.Remainder.PaidAmount > 0.009m
            || pair.Remainder.Status == PaymentStatus.Completed
            || await ClientBalanceService.GetAppliedCreditAmountAsync(_db, pair.Remainder.Id, ct) > AmountTolerance)
        {
            return Conflict(new { message = PaymentRemainderTransferRules.RemainderAlreadyProcessedMessage });
        }

        var nestedRemainder = await FindPartialRemainderPaymentAsync(pair.Remainder.Id, ct);
        if (nestedRemainder is not null)
        {
            return Conflict(new { message = PaymentRemainderTransferRules.RemainderAlreadyProcessedMessage });
        }

        return null;
    }

    private async Task<ActionResult?> ValidatePartialRemainderUpdateAsync(Payment current, Payment model, CancellationToken ct)
        => await PaymentRemainderWriteRules.ValidateUpdateAsync(_db, current, model, ct) is { } error
            ? Conflict(new { message = error }) : null;

    private readonly record struct PartialRemainderTransferPair(
        Payment Source,
        Payment Remainder,
        PaymentRemainderTransferRules.PartialRemainderMarkerData Marker,
        int? TransferId);

    private static void AppendSourceRemainderAuditNotes(
        Payment source,
        decimal originalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate,
        DateTime now,
        string? actor,
        TimeZoneInfo paymentTimeZone)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        PaymentDomainService.AppendSystemNote(
            source,
            $"Частичная оплата: {paidAmount.ToString("N2", culture)} / {originalAmount.ToString("N2", culture)}. Остаток {remainderAmount.ToString("N2", culture)} перенесен на {remainderDate:dd.MM.yyyy}.",
            now,
            actor,
            paymentTimeZone);
    }

    private static Payment CreateRemainderPaymentFromSource(
        Payment source,
        decimal originalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate,
        DateTime now,
        TimeZoneInfo paymentTimeZone)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var description = string.IsNullOrWhiteSpace(source.Description)
            ? "Остаток после частичной оплаты"
            : $"{source.Description.Trim()} · остаток";
        var marker = PaymentRemainderTransferRules.BuildMarker(
            source.Id,
            originalAmount,
            paidAmount,
            remainderAmount,
            remainderDate);

        var localToday = PaymentDomainService.ResolveLocalToday(now, paymentTimeZone);

        return new Payment
        {
            Account = source.Account,
            AccountDate = source.AccountDate,
            InvoiceId = source.InvoiceId,
            LegacyAccountNumber = source.LegacyAccountNumber,
            LegacyAccountDate = source.LegacyAccountDate,
            Amount = remainderAmount,
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            ExpenseClientId = source.ExpenseClientId,
            ExpenseLegalEntityId = source.ExpenseLegalEntityId,
            ClientCaseId = source.ClientCaseId,
            ClientCaseReference = source.ClientCaseReference,
            ContractId = source.ContractId,
            DocumentKind = DocumentKind.Payment,
            CreatedAt = now,
            Date = remainderDate.Date,
            PlannedDate = remainderDate.Date,
            DealTypeId = source.DealTypeId,
            Description = description,
            CounterpartyName = source.CounterpartyName,
            IncomeTypeId = source.IncomeTypeId,
            IsPaid = false,
            PaidAmount = 0m,
            LastPaymentDate = null,
            Notes = source.Notes,
            IsNotForAccounting = source.IsNotForAccounting,
            SystemNotes = string.Join('\n', new[]
            {
                marker,
                $"Остаток после частичной оплаты платежа #{source.Id}: исходно {originalAmount.ToString("N2", culture)}, оплачено {paidAmount.ToString("N2", culture)}, остаток {remainderAmount.ToString("N2", culture)}.",
            }),
            RescheduleCount = Math.Max(0, source.RescheduleCount) + 1,
            PaidDate = null,
            PaymentSourceId = source.PaymentSourceId,
            ServiceId = source.ServiceId,
            PaymentStatusId = source.PaymentStatusId,
            Status = remainderDate.Date < localToday ? PaymentStatus.Overdue : PaymentStatus.Pending,
            Type = source.Type,
        };
    }

    private async Task CopyPaymentResponsiblesAsync(Payment target, IEnumerable<int> responsibleIds, CancellationToken ct)
    {
        var ids = responsibleIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var existingUserIds = await _db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => user.Id)
            .ToListAsync(ct);

        foreach (var userId in existingUserIds)
        {
            target.Responsibles.Add(new PaymentResponsible
            {
                UserId = userId,
            });
        }
    }

    private static void CopyPaymentInvoiceClientCases(Payment target, IEnumerable<int> clientCaseIds)
    {
        foreach (var clientCaseId in clientCaseIds.Where(id => id > 0).Distinct())
        {
            target.InvoiceClientCases.Add(new InvoiceClientCase
            {
                ClientCaseId = clientCaseId,
            });
        }
    }

    private async Task SyncInvoiceManagedPaymentBindingsAsync(Payment payment, CancellationToken ct)
    {
        if (!(InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment)
              || InvoiceManagedPaymentRules.IsGeneratedInvoiceRemainderPayment(payment))
            || !payment.InvoiceId.HasValue
            || payment.InvoiceId.Value <= 0)
        {
            return;
        }

        var representative = await _db.Payments
            .Include(item => item.Responsibles)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.InvoiceId == payment.InvoiceId.Value
                    && item.DocumentKind == DocumentKind.Invoice,
                ct);
        if (representative is null)
        {
            return;
        }

        payment.Account = representative.Account;
        payment.AccountDate = representative.AccountDate;
        payment.Type = representative.Type;
        payment.ClientId = representative.ClientId;
        payment.LegalEntityId = representative.LegalEntityId;
        payment.ExpenseClientId = representative.ExpenseClientId;
        payment.ExpenseLegalEntityId = representative.ExpenseLegalEntityId;
        payment.ContractId = representative.ContractId;
        payment.ClientCaseId = representative.ClientCaseId;
        payment.ClientCaseReference = representative.ClientCaseReference;
        payment.PaymentSourceId ??= representative.PaymentSourceId;
        payment.ServiceId ??= representative.ServiceId;
        payment.IncomeTypeId ??= representative.IncomeTypeId;
        payment.DealTypeId ??= representative.DealTypeId;
        payment.PaymentStatusId ??= representative.PaymentStatusId;
        payment.CounterpartyName = null;

        await SyncPaymentResponsiblesAsync(payment, representative.Responsibles.Select(item => item.UserId), ct);
    }

    private async Task SyncPaymentResponsiblesAsync(Payment payment, IEnumerable<int> responsibleIds, CancellationToken ct)
    {
        var collection = _db.Entry(payment).Collection(item => item.Responsibles);
        if (!collection.IsLoaded)
        {
            await collection.LoadAsync(ct);
        }

        var targetIds = responsibleIds.Where(id => id > 0).Distinct().ToHashSet();
        foreach (var existing in payment.Responsibles.Where(item => !targetIds.Contains(item.UserId)).ToList())
        {
            payment.Responsibles.Remove(existing);
        }

        var existingIds = payment.Responsibles.Select(item => item.UserId).ToHashSet();
        foreach (var responsibleId in targetIds)
        {
            if (existingIds.Contains(responsibleId))
            {
                continue;
            }

            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }
    }

    [HttpPost("repair-invoice-links")]
    public async Task<IActionResult> RepairInvoiceLinks(CancellationToken ct)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(User, ct)) return Forbid();
        if (await _db.Payments.Where(payment => payment.Invoice != null).HasAmbiguousInvoiceLinksAsync(ct))
            return Conflict(new { message = InvoiceQueryExtensions.ConflictMessage });
        var payments = await _db.Payments
            .Where(p => (p.Account != null && p.Account != "") || p.InvoiceId != null)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var originalInvoiceIds = payments.ToDictionary(p => p.Id, p => p.InvoiceId);
        var originalKinds = payments.ToDictionary(p => p.Id, p => p.DocumentKind);
        var repairedLinks = 0;
        var clearedLinks = 0;
        var correctedKinds = 0;

        foreach (var payment in payments)
        {
            await SyncInvoiceLinkAsync(payment, ct);
        }

        var invoiceDocumentKeeperIds = payments
            .Where(p => p.DocumentKind == DocumentKind.Invoice && p.InvoiceId.HasValue)
            .GroupBy(p => p.InvoiceId!.Value)
            .Select(group => group.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).First().Id)
            .ToHashSet();

        foreach (var payment in payments)
        {
            if (payment.DocumentKind == DocumentKind.Invoice
                && payment.InvoiceId.HasValue
                && !invoiceDocumentKeeperIds.Contains(payment.Id))
            {
                payment.DocumentKind = DocumentKind.Payment;
            }

            if (payment.InvoiceId != originalInvoiceIds[payment.Id])
            {
                if (payment.InvoiceId.HasValue)
                {
                    repairedLinks++;
                }
                else
                {
                    clearedLinks++;
                }
            }

            if (payment.DocumentKind != originalKinds[payment.Id])
            {
                correctedKinds++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            scanned = payments.Count,
            repairedLinks,
            clearedLinks,
            correctedKinds,
        });
    }

    private async Task SyncInvoiceLinkAsync(Payment payment, CancellationToken ct)
    {
        var normalizedAccount = NormalizeInvoiceNumber(payment.Account);
        var originalAccountDate = (payment.AccountDate ?? payment.Date).Date;
        var invoiceDocument = await ResolveInvoiceDocumentAsync(payment, ct);
        var invoice = invoiceDocument?.Invoice;
        if (invoice is null)
        {
            payment.InvoiceId = null;

            payment.Account = normalizedAccount;
            payment.AccountDate = normalizedAccount is null
                ? null
                : originalAccountDate;
            if (normalizedAccount is null)
            {
                payment.LegacyAccountNumber = null;
                payment.LegacyAccountDate = null;
            }
            await SyncPaymentInvoiceClientCasesAsync(payment, [], ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(payment.LegacyAccountNumber) && normalizedAccount is not null)
        {
            var normalizedInvoiceNumber = NormalizeInvoiceNumber(invoice.Number);
            if (!string.Equals(normalizedAccount, normalizedInvoiceNumber, StringComparison.OrdinalIgnoreCase)
                || originalAccountDate != invoice.Date.Date)
            {
                payment.LegacyAccountNumber = normalizedAccount;
                payment.LegacyAccountDate = originalAccountDate;
            }
        }

        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date.Date;
        if (invoiceDocument is not null)
        {
            payment.Type = invoiceDocument.Type;
            payment.ClientId = invoiceDocument.ClientId;
            payment.LegalEntityId = invoiceDocument.LegalEntityId;
            payment.ExpenseClientId = invoiceDocument.ExpenseClientId;
            payment.ExpenseLegalEntityId = invoiceDocument.ExpenseLegalEntityId;
            payment.ContractId = invoiceDocument.ContractId;

            var linkedCaseIds = invoiceDocument.InvoiceClientCases
                .Select(link => link.ClientCaseId)
                .Concat(invoiceDocument.ClientCaseId.HasValue ? [invoiceDocument.ClientCaseId.Value] : [])
                .Where(caseId => caseId > 0)
                .Distinct()
                .ToList();

            var firstCaseId = linkedCaseIds.FirstOrDefault();
            payment.ClientCaseId = firstCaseId > 0 ? firstCaseId : null;

            await SyncPaymentInvoiceClientCasesAsync(payment, linkedCaseIds, ct);
        }
        else
        {
            await SyncPaymentInvoiceClientCasesAsync(payment, [], ct);
        }
    }

    private async Task<Payment?> ResolveInvoiceDocumentAsync(Payment payment, CancellationToken ct)
    {
        if (payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
        {
            var linked = await _db.Payments
                .AsNoTracking()
                .ApplyUnambiguousInvoiceLinks()
                .Include(x => x.Invoice)
                .Include(x => x.InvoiceClientCases)
                .FirstOrDefaultAsync(x => x.InvoiceId == payment.InvoiceId.Value && x.DocumentKind == DocumentKind.Invoice, ct);
            if (linked is not null)
            {
                return linked;
            }

            var invoice = await _db.Invoices
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payment.InvoiceId.Value, ct);
            if (invoice is not null)
            {
                return new Payment
                {
                    InvoiceId = invoice.Id,
                    Invoice = invoice,
                    Type = payment.Type,
                    ClientId = payment.ClientId,
                    LegalEntityId = payment.LegalEntityId,
                    ExpenseClientId = payment.ExpenseClientId,
                    ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
                    ContractId = payment.ContractId,
                    ClientCaseId = payment.ClientCaseId,
                };
            }
        }

        var lookupCandidates = BuildLookupCandidates(payment);
        if (lookupCandidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in lookupCandidates)
        {
            var resolved = await ResolveInvoiceByPaymentHistoryAsync(payment, candidate.Number, candidate.Date, ct);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? NormalizeInvoiceNumber(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<ActionResult?> ValidateFinancialActionReasonAsync(string? reason, string actionName, CancellationToken ct)
    {
        if (!await User.RequiresFinancialActionReasonAsync(_db, ct))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(reason)
            ? BadRequest($"Укажите причину {actionName}. Она будет сохранена в журнале.")
            : null;
    }

    private void ApplyAuditReason(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            _db.SetAuditReason(reason);
        }
    }

    private static bool AreAmountsEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= 0.009m;

    private async Task<Payment?> ResolveInvoiceByPaymentHistoryAsync(Payment payment, string lookupNumber, DateTime lookupDate, CancellationToken ct)
    {
        if (!payment.ClientId.HasValue || payment.ClientId.Value <= 0)
        {
            return null;
        }

        var normalizedLookup = lookupNumber.ToLowerInvariant();
        var caseId = payment.ClientCaseId is > 0 ? payment.ClientCaseId : null;
        var lookupYear = lookupDate.Year;

        var candidates = await _db.Payments
            .AsNoTracking()
            .ApplyUnambiguousInvoiceLinks()
            .Where(x => x.InvoiceId.HasValue && x.ClientId == payment.ClientId.Value && x.Type == payment.Type)
            .Where(x => caseId.HasValue
                ? x.ClientCaseId == caseId.Value || x.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value)
                : x.ClientCaseId == null && !x.InvoiceClientCases.Any())
            .Where(x =>
                (x.LegacyAccountNumber != null
                    && x.LegacyAccountNumber.ToLower() == normalizedLookup
                    && (x.LegacyAccountDate ?? x.AccountDate ?? x.Date).Year == lookupYear)
                || (x.Account != null
                    && x.Account.ToLower() == normalizedLookup
                    && (x.AccountDate ?? x.Date).Year == lookupYear))
            .Select(x => new
            {
                InvoiceId = x.InvoiceId!.Value,
                ExactLegacyDate = x.LegacyAccountNumber != null
                    && x.LegacyAccountNumber.ToLower() == normalizedLookup
                    && x.LegacyAccountDate.HasValue
                    && x.LegacyAccountDate.Value.Date == lookupDate.Date,
                ExactAccountDate = x.Account != null
                    && x.Account.ToLower() == normalizedLookup
                    && x.AccountDate.HasValue
                    && x.AccountDate.Value.Date == lookupDate.Date,
                IsInvoiceDocument = x.DocumentKind == DocumentKind.Invoice,
                x.CreatedAt,
                x.Id,
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return null;
        }

        var invoiceId = candidates
            .GroupBy(x => x.InvoiceId)
            .OrderByDescending(group => group.Max(item => item.ExactLegacyDate ? 3 : item.ExactAccountDate ? 2 : item.IsInvoiceDocument ? 1 : 0))
            .ThenByDescending(group => group.Count())
            .ThenByDescending(group => group.Max(item => item.CreatedAt))
            .ThenByDescending(group => group.Max(item => item.Id))
            .Select(group => group.Key)
            .FirstOrDefault();

        if (invoiceId <= 0)
        {
            return null;
        }

        return await _db.Payments
            .AsNoTracking()
            .Include(x => x.Invoice)
            .Include(x => x.InvoiceClientCases)
            .FirstOrDefaultAsync(x => x.InvoiceId == invoiceId && x.DocumentKind == DocumentKind.Invoice, ct);
    }

    private async Task PopulateMissingExpenseAttributionFromCaseAsync(Payment payment, CancellationToken ct)
    {
        if (payment.Type != PaymentType.Expense
            || !payment.ClientCaseId.HasValue
            || payment.ClientCaseId.Value <= 0
            || (payment.ExpenseClientId.HasValue && payment.ExpenseClientId.Value > 0)
            || (payment.ExpenseLegalEntityId.HasValue && payment.ExpenseLegalEntityId.Value > 0))
        {
            return;
        }

        var owner = await _db.ClientCases
            .AsNoTracking()
            .Where(item => item.Id == payment.ClientCaseId.Value)
            .Select(item => new { item.ClientId, item.LegalEntityId })
            .FirstOrDefaultAsync(ct);

        if (owner is null)
        {
            return;
        }

        if (owner.ClientId.HasValue && owner.ClientId.Value > 0)
        {
            payment.ExpenseClientId = owner.ClientId.Value;
            payment.ExpenseLegalEntityId = null;
        }
        else if (owner.LegalEntityId.HasValue && owner.LegalEntityId.Value > 0)
        {
            payment.ExpenseClientId = null;
            payment.ExpenseLegalEntityId = owner.LegalEntityId.Value;
        }
    }

    private async Task<ActionResult?> ValidatePaymentAttributionAsync(Payment payment, CancellationToken ct)
    {
        var clientsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);
        var legalEntitiesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.LegalEntities, ct);

        var hasClient = payment.ClientId.HasValue && payment.ClientId.Value > 0;
        var hasLegalEntity = payment.LegalEntityId.HasValue && payment.LegalEntityId.Value > 0;
        if (hasClient && hasLegalEntity)
        {
            return BadRequest("Payment must reference either clientId or legalEntityId, not both.");
        }

        if (hasClient)
        {
            var clientExists = await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(clientsVisibility)
                .AnyAsync(client => client.Id == payment.ClientId!.Value, ct);
            if (!clientExists)
            {
                return BadRequest($"clientId {payment.ClientId!.Value} is not available");
            }
        }

        if (hasLegalEntity)
        {
            var legalEntityExists = await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(legalEntitiesVisibility)
                .AnyAsync(entity => entity.Id == payment.LegalEntityId!.Value, ct);
            if (!legalEntityExists)
            {
                return BadRequest($"legalEntityId {payment.LegalEntityId!.Value} is not available");
            }
        }

        var hasExpenseClient = payment.ExpenseClientId.HasValue && payment.ExpenseClientId.Value > 0;
        var hasExpenseLegalEntity = payment.ExpenseLegalEntityId.HasValue && payment.ExpenseLegalEntityId.Value > 0;
        if (hasExpenseClient && hasExpenseLegalEntity)
        {
            return BadRequest("Expense payment must reference either expenseClientId or expenseLegalEntityId, not both.");
        }

        if (payment.Type == PaymentType.Expense && hasExpenseClient)
        {
            var expenseClientExists = await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(clientsVisibility)
                .AnyAsync(client => client.Id == payment.ExpenseClientId!.Value, ct);
            if (!expenseClientExists)
            {
                return BadRequest($"expenseClientId {payment.ExpenseClientId!.Value} is not available");
            }
        }

        if (payment.Type == PaymentType.Expense && hasExpenseLegalEntity)
        {
            var expenseLegalEntityExists = await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(legalEntitiesVisibility)
                .AnyAsync(entity => entity.Id == payment.ExpenseLegalEntityId!.Value, ct);
            if (!expenseLegalEntityExists)
            {
                return BadRequest($"expenseLegalEntityId {payment.ExpenseLegalEntityId!.Value} is not available");
            }
        }

        if (!payment.ClientCaseId.HasValue || payment.ClientCaseId.Value <= 0)
        {
            return null;
        }

        var caseOwnerClientId = payment.Type == PaymentType.Expense
            ? payment.ExpenseClientId
            : payment.ClientId;
        var caseOwnerLegalEntityId = payment.Type == PaymentType.Expense
            ? payment.ExpenseLegalEntityId
            : payment.LegalEntityId;
        var hasCaseOwnerClient = caseOwnerClientId.HasValue && caseOwnerClientId.Value > 0;
        var hasCaseOwnerLegalEntity = caseOwnerLegalEntityId.HasValue && caseOwnerLegalEntityId.Value > 0;
        if (hasCaseOwnerClient == hasCaseOwnerLegalEntity)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest("ExpenseClientId or expenseLegalEntityId is required when clientCaseId is specified for expense payments.")
                : BadRequest("ClientId or legalEntityId is required when clientCaseId is specified.");
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(casesVisibility)
            .FirstOrDefaultAsync(item => item.Id == payment.ClientCaseId.Value, ct);
        if (clientCase is null)
        {
            return BadRequest($"clientCaseId {payment.ClientCaseId.Value} is not available");
        }

        if (hasCaseOwnerClient && clientCase.ClientId != caseOwnerClientId!.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseClientId {caseOwnerClientId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to clientId {caseOwnerClientId.Value}");
        }

        if (hasCaseOwnerLegalEntity && clientCase.LegalEntityId != caseOwnerLegalEntityId!.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseLegalEntityId {caseOwnerLegalEntityId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to legalEntityId {caseOwnerLegalEntityId.Value}");
        }

        return null;
    }

    private async Task SyncPaymentInvoiceClientCasesAsync(Payment payment, IReadOnlyCollection<int> clientCaseIds, CancellationToken ct)
    {
        var targetIds = clientCaseIds
            .Where(caseId => caseId > 0)
            .Distinct()
            .ToHashSet();

        var entry = _db.Entry(payment);
        if (entry.State != EntityState.Detached)
        {
            var collection = entry.Collection(item => item.InvoiceClientCases);
            if (!collection.IsLoaded)
            {
                await collection.LoadAsync(ct);
            }
        }

        var toRemove = payment.InvoiceClientCases
            .Where(link => !targetIds.Contains(link.ClientCaseId))
            .ToList();
        foreach (var link in toRemove)
        {
            payment.InvoiceClientCases.Remove(link);
        }

        var existingIds = payment.InvoiceClientCases
            .Select(link => link.ClientCaseId)
            .ToHashSet();
        foreach (var clientCaseId in targetIds)
        {
            if (existingIds.Contains(clientCaseId))
            {
                continue;
            }

            payment.InvoiceClientCases.Add(new InvoiceClientCase
            {
                PaymentId = payment.Id,
                ClientCaseId = clientCaseId,
            });
        }
    }

    private static List<(string Number, DateTime Date)> BuildLookupCandidates(Payment payment)
    {
        var candidates = new List<(string Number, DateTime Date)>();

        void Add(string? number, DateTime? date)
        {
            var normalized = NormalizeInvoiceNumber(number);
            if (normalized is null)
            {
                return;
            }

            var effectiveDate = (date ?? payment.Date).Date;
            if (candidates.Any(item => string.Equals(item.Number, normalized, StringComparison.OrdinalIgnoreCase) && item.Date == effectiveDate))
            {
                return;
            }

            candidates.Add((normalized, effectiveDate));
        }

        Add(payment.LegacyAccountNumber, payment.LegacyAccountDate);
        Add(payment.Account, payment.AccountDate);

        return candidates;
    }

    private static string MergeSystemNotes(string original, string? incoming)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AppendLines(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            foreach (var line in source.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (seen.Add(trimmed)) lines.Add(trimmed);
            }
        }

        AppendLines(original);
        AppendLines(incoming ?? string.Empty);

        return string.Join("\n", lines);
    }

    private static void SyncPlannedDateWithChangedDueDate(
        Payment payment,
        DateTime originalDueDate,
        DateTime originalPlannedDate,
        Payment incoming)
    {
        if (incoming.Date.Date == originalDueDate)
        {
            return;
        }

        var incomingPlannedDate = incoming.PlannedDate == default
            ? (DateTime?)null
            : incoming.PlannedDate.Date;
        var plannedDateChangedExplicitly =
            incomingPlannedDate.HasValue && incomingPlannedDate.Value != originalPlannedDate;

        if (!plannedDateChangedExplicitly)
        {
            payment.PlannedDate = incoming.Date.Date;
        }
    }

    private static void SyncInstallmentScheduleMetadataDate(Payment payment)
    {
        if (!InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment))
        {
            return;
        }

        var scheduleDate = PaymentDomainService.NormalizeLocalDate(
            payment.PlannedDate == default ? payment.Date : payment.PlannedDate);
        payment.SystemNotes = ReplaceInstallmentMetadataDate(payment.SystemNotes, scheduleDate);
    }

    private static string ReplaceInstallmentMetadataDate(string? notes, DateTime scheduleDate)
    {
        var lines = SplitSystemNoteLines(notes).ToList();
        var markerIndex = lines.FindIndex(line =>
            line.StartsWith(InvoiceManagedPaymentRules.InstallmentPaymentMarker, StringComparison.Ordinal));
        if (markerIndex < 0)
        {
            return string.Join("\n", lines);
        }

        var line = lines[markerIndex];
        var json = line[InvoiceManagedPaymentRules.InstallmentPaymentMarker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
        {
            return string.Join("\n", lines);
        }

        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null)
            {
                return string.Join("\n", lines);
            }

            var item = GetObjectProperty(root, "item");
            if (item is null)
            {
                return string.Join("\n", lines);
            }

            var normalizedDate = scheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            SetPropertyCaseInsensitive(item, "date", normalizedDate);
            if (TryGetIntegerProperty(root, "sequence", out var sequence) && sequence == 1)
            {
                SetPropertyCaseInsensitive(root, "startDate", normalizedDate);
            }

            lines[markerIndex] = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} {root.ToJsonString()}";
            return string.Join("\n", lines);
        }
        catch (JsonException)
        {
            return string.Join("\n", lines);
        }
        catch (InvalidOperationException)
        {
            return string.Join("\n", lines);
        }
    }

    private static JsonObject? GetObjectProperty(JsonObject source, string name)
    {
        foreach (var property in source)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)
                && property.Value is JsonObject obj)
            {
                return obj;
            }
        }

        return null;
    }

    private static void SetPropertyCaseInsensitive(JsonObject source, string name, string value)
    {
        var key = source
            .Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            ?? name;
        source[key] = value;
    }

    private static bool TryGetIntegerProperty(JsonObject source, string name, out int value)
    {
        foreach (var property in source)
        {
            if (!string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value is not null && property.Value.GetValueKind() == JsonValueKind.Number)
            {
                value = property.Value.GetValue<int>();
                return true;
            }

            break;
        }

        value = 0;
        return false;
    }

    private static string ReplaceTechnicalMarkerLine(string? notes, string marker, string replacement)
    {
        var lines = SplitSystemNoteLines(notes)
            .Where(line => !line.StartsWith(marker, StringComparison.Ordinal))
            .ToList();

        if (!string.IsNullOrWhiteSpace(replacement))
        {
            lines.Insert(0, replacement.Trim());
        }

        return string.Join("\n", lines);
    }

    private static string RemoveTechnicalMarkerLines(string? notes, params string[] markers)
    {
        var markerSet = markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .ToArray();

        return string.Join(
            "\n",
            SplitSystemNoteLines(notes)
                .Where(line => !markerSet.Any(marker => line.StartsWith(marker, StringComparison.Ordinal))));
    }

    private static IEnumerable<string> SplitSystemNoteLines(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return Enumerable.Empty<string>();
        }

        return notes
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static void PopulateAuditMetadata(PaymentContext db, Payment payment)
    {
        var entry = db.Entry(payment);
        if (entry.Metadata.FindProperty("CreatedByUserId") is null)
        {
            return;
        }

        payment.AuditCreatedByUserId = entry.Property<int?>("CreatedByUserId").CurrentValue;
        payment.AuditCreatedByUserName = entry.Property<string?>("CreatedByUserName").CurrentValue;
        payment.AuditUpdatedAt = entry.Property<DateTime?>("UpdatedAt").CurrentValue;
        payment.AuditUpdatedByUserId = entry.Property<int?>("UpdatedByUserId").CurrentValue;
        payment.AuditUpdatedByUserName = entry.Property<string?>("UpdatedByUserName").CurrentValue;
        payment.AuditIsDeleted = entry.Property<bool>("IsDeleted").CurrentValue;
        payment.AuditDeletedAt = entry.Property<DateTime?>("DeletedAt").CurrentValue;
        payment.AuditDeletedByUserId = entry.Property<int?>("DeletedByUserId").CurrentValue;
        payment.AuditDeletedByUserName = entry.Property<string?>("DeletedByUserName").CurrentValue;
    }

    private async Task AttachContractorRecipientsAsync(IReadOnlyCollection<Payment> payments, CancellationToken ct)
    {
        if (payments.Count == 0)
        {
            return;
        }

        var paymentIds = payments
            .Select(payment => payment.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (paymentIds.Length == 0)
        {
            return;
        }

        var contractorLinks = await _db.TaskJournalItems
            .AsNoTracking()
            .Where(task => task.ContractorPaymentId.HasValue && paymentIds.Contains(task.ContractorPaymentId.Value))
            .Select(task => new
            {
                PaymentId = task.ContractorPaymentId!.Value,
                UserId = task.AssigneeUserId,
                UserName = task.AssigneeName,
            })
            .ToListAsync(ct);

        var linksByPaymentId = contractorLinks
            .GroupBy(link => link.PaymentId)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(link => link.UserId.HasValue) ?? group.First());

        foreach (var payment in payments)
        {
            if (!linksByPaymentId.TryGetValue(payment.Id, out var link))
            {
                continue;
            }

            payment.ContractorUserId = link.UserId;
            payment.ContractorUserName = string.IsNullOrWhiteSpace(link.UserName) ? payment.CounterpartyName : link.UserName.Trim();
        }
    }

    private static IQueryable<Payment> ProjectPaymentsForRead(IQueryable<Payment> query)
    {
        return query
            .AsNoTracking()
            .Select(payment => new Payment
            {
                Id = payment.Id,
                Account = payment.Account,
                AccountDate = payment.AccountDate,
                Amount = payment.Amount,
                ClientCaseId = payment.ClientCaseId,
                ClientCaseReference = payment.ClientCaseReference,
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                ContractId = payment.ContractId,
                CounterpartyName = payment.CounterpartyName,
                CreatedAt = payment.CreatedAt,
                Date = payment.Date,
                DealTypeId = payment.DealTypeId,
                Description = payment.Description,
                DocumentKind = payment.DocumentKind,
                ExpenseClientId = payment.ExpenseClientId,
                ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
                IncomeTypeId = payment.IncomeTypeId,
                InvoiceId = payment.InvoiceId,
                IsPaid = payment.IsPaid,
                LastPaymentDate = payment.LastPaymentDate,
                LegacyAccountDate = payment.LegacyAccountDate,
                LegacyAccountNumber = payment.LegacyAccountNumber,
                Notes = payment.Notes,
                PaidAmount = payment.PaidAmount,
                AdvanceReviewStatus = payment.AdvanceReviewStatus,
                AdvanceReviewReason = payment.AdvanceReviewReason,
                AdvanceReviewedAt = payment.AdvanceReviewedAt,
                AdvanceReviewedByUserId = payment.AdvanceReviewedByUserId,
                AdvanceReviewedByUserName = payment.AdvanceReviewedByUserName,
                PaidDate = payment.PaidDate,
                PaymentDocumentDate = payment.PaymentDocumentDate,
                PaymentOrderNumber = payment.PaymentOrderNumber,
                PaymentSourceId = payment.PaymentSourceId,
                PaymentStatusId = payment.PaymentStatusId,
                PlannedDate = payment.PlannedDate,
                RescheduleCount = payment.RescheduleCount,
                ServiceId = payment.ServiceId,
                Status = payment.Status,
                SystemNotes = payment.SystemNotes,
                Type = payment.Type,
                AuditCreatedByUserId = EF.Property<int?>(payment, "CreatedByUserId"),
                AuditCreatedByUserName = EF.Property<string?>(payment, "CreatedByUserName"),
                AuditUpdatedAt = EF.Property<DateTime?>(payment, "UpdatedAt"),
                AuditUpdatedByUserId = EF.Property<int?>(payment, "UpdatedByUserId"),
                AuditUpdatedByUserName = EF.Property<string?>(payment, "UpdatedByUserName"),
                AuditIsDeleted = EF.Property<bool>(payment, "IsDeleted"),
                AuditDeletedAt = EF.Property<DateTime?>(payment, "DeletedAt"),
                AuditDeletedByUserId = EF.Property<int?>(payment, "DeletedByUserId"),
                AuditDeletedByUserName = EF.Property<string?>(payment, "DeletedByUserName"),
                Client = payment.Client == null
                    ? null
                    : new Client
                    {
                        Id = payment.Client.Id,
                        Name = payment.Client.Name,
                        Email = payment.Client.Email,
                        Phone = payment.Client.Phone,
                        Company = payment.Client.Company,
                        Address = payment.Client.Address,
                        Notes = payment.Client.Notes,
                        CreatedAt = payment.Client.CreatedAt,
                        IsActive = payment.Client.IsActive,
                        LegalEntityId = payment.Client.LegalEntityId,
                        ClientStatusId = payment.Client.ClientStatusId,
                        ResponsibleUserId = payment.Client.ResponsibleUserId,
                        ClientStatus = payment.Client.ClientStatus == null
                            ? null
                            : new ClientStatusEntity
                            {
                                Id = payment.Client.ClientStatus.Id,
                                Name = payment.Client.ClientStatus.Name,
                                Description = payment.Client.ClientStatus.Description,
                                ColorHex = payment.Client.ClientStatus.ColorHex,
                                IsActive = payment.Client.ClientStatus.IsActive,
                                CreatedAt = payment.Client.ClientStatus.CreatedAt,
                                SystemKey = payment.Client.ClientStatus.SystemKey,
                            },
                    },
                ClientCase = payment.ClientCase == null
                    ? null
                    : new ClientCase
                    {
                        Id = payment.ClientCase.Id,
                        ClientId = payment.ClientCase.ClientId,
                        LegalEntityId = payment.ClientCase.LegalEntityId,
                        Title = payment.ClientCase.Title,
                        Description = payment.ClientCase.Description,
                        Status = payment.ClientCase.Status,
                        CreatedAt = payment.ClientCase.CreatedAt,
                    },
                LegalEntity = payment.LegalEntity == null
                    ? null
                    : new LegalEntity
                    {
                        Id = payment.LegalEntity.Id,
                        ShortName = payment.LegalEntity.ShortName,
                        FullName = payment.LegalEntity.FullName,
                        Inn = payment.LegalEntity.Inn,
                        Kpp = payment.LegalEntity.Kpp,
                        Ogrn = payment.LegalEntity.Ogrn,
                        Address = payment.LegalEntity.Address,
                        Phone = payment.LegalEntity.Phone,
                        Email = payment.LegalEntity.Email,
                        CreatedAt = payment.LegalEntity.CreatedAt,
                    },
                ExpenseClient = payment.ExpenseClient == null
                    ? null
                    : new Client
                    {
                        Id = payment.ExpenseClient.Id,
                        Name = payment.ExpenseClient.Name,
                        Email = payment.ExpenseClient.Email,
                        Phone = payment.ExpenseClient.Phone,
                        Company = payment.ExpenseClient.Company,
                        Address = payment.ExpenseClient.Address,
                        Notes = payment.ExpenseClient.Notes,
                        CreatedAt = payment.ExpenseClient.CreatedAt,
                        IsActive = payment.ExpenseClient.IsActive,
                        LegalEntityId = payment.ExpenseClient.LegalEntityId,
                        ClientStatusId = payment.ExpenseClient.ClientStatusId,
                        ResponsibleUserId = payment.ExpenseClient.ResponsibleUserId,
                        ClientStatus = payment.ExpenseClient.ClientStatus == null
                            ? null
                            : new ClientStatusEntity
                            {
                                Id = payment.ExpenseClient.ClientStatus.Id,
                                Name = payment.ExpenseClient.ClientStatus.Name,
                                Description = payment.ExpenseClient.ClientStatus.Description,
                                ColorHex = payment.ExpenseClient.ClientStatus.ColorHex,
                                IsActive = payment.ExpenseClient.ClientStatus.IsActive,
                                CreatedAt = payment.ExpenseClient.ClientStatus.CreatedAt,
                                SystemKey = payment.ExpenseClient.ClientStatus.SystemKey,
                            },
                    },
                ExpenseLegalEntity = payment.ExpenseLegalEntity == null
                    ? null
                    : new LegalEntity
                    {
                        Id = payment.ExpenseLegalEntity.Id,
                        ShortName = payment.ExpenseLegalEntity.ShortName,
                        FullName = payment.ExpenseLegalEntity.FullName,
                        Inn = payment.ExpenseLegalEntity.Inn,
                        Kpp = payment.ExpenseLegalEntity.Kpp,
                        Ogrn = payment.ExpenseLegalEntity.Ogrn,
                        Address = payment.ExpenseLegalEntity.Address,
                        Phone = payment.ExpenseLegalEntity.Phone,
                        Email = payment.ExpenseLegalEntity.Email,
                        CreatedAt = payment.ExpenseLegalEntity.CreatedAt,
                    },
                DealType = payment.DealType == null
                    ? null
                    : new DealType
                    {
                        Id = payment.DealType.Id,
                        Name = payment.DealType.Name,
                        Description = payment.DealType.Description,
                        ColorHex = payment.DealType.ColorHex,
                        IsActive = payment.DealType.IsActive,
                        CreatedAt = payment.DealType.CreatedAt,
                    },
                IncomeType = payment.IncomeType == null
                    ? null
                    : new IncomeType
                    {
                        Id = payment.IncomeType.Id,
                        Name = payment.IncomeType.Name,
                        Description = payment.IncomeType.Description,
                        ColorHex = payment.IncomeType.ColorHex,
                        IsActive = payment.IncomeType.IsActive,
                        CreatedAt = payment.IncomeType.CreatedAt,
                        PaymentType = payment.IncomeType.PaymentType,
                    },
                Service = payment.Service == null
                    ? null
                    : new ServiceDictionaryItem
                    {
                        Id = payment.Service.Id,
                        Name = payment.Service.Name,
                        Description = payment.Service.Description,
                        Unit = payment.Service.Unit,
                        DefaultPrice = payment.Service.DefaultPrice,
                        CategoryId = payment.Service.CategoryId,
                        ColorHex = payment.Service.ColorHex,
                        IsActive = payment.Service.IsActive,
                        CreatedAt = payment.Service.CreatedAt,
                    },
                PaymentSource = payment.PaymentSource == null
                    ? null
                    : new PaymentSource
                    {
                        Id = payment.PaymentSource.Id,
                        Name = payment.PaymentSource.Name,
                        Description = payment.PaymentSource.Description,
                        ColorHex = payment.PaymentSource.ColorHex,
                        IsActive = payment.PaymentSource.IsActive,
                        CreatedAt = payment.PaymentSource.CreatedAt,
                    },
                PaymentStatusEntity = payment.PaymentStatusEntity == null
                    ? null
                    : new PaymentStatusEntity
                    {
                        Id = payment.PaymentStatusEntity.Id,
                        Name = payment.PaymentStatusEntity.Name,
                        Description = payment.PaymentStatusEntity.Description,
                        ColorHex = payment.PaymentStatusEntity.ColorHex,
                        IsActive = payment.PaymentStatusEntity.IsActive,
                        CreatedAt = payment.PaymentStatusEntity.CreatedAt,
                    },
            });
    }

    [HttpDelete("{id:int}")]
    public Task<IActionResult> Delete(int id, CancellationToken ct)
        => RunPaymentWriteAsync(id, _ => DeleteCoreAsync(id, ct), ct, deleting: true);

    private async Task<IActionResult> DeleteCoreAsync(int id, CancellationToken ct)
    {
        if (await RequirePaymentsPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null)
        {
            return AccessDenied();
        }
        if (!await CanAccessPaymentAsync(id, visibility, ct))
        {
            return NotFound();
        }

        var payment = await _db.Payments
            .Include(item => item.Invoice)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (payment is null) return NotFound();

        if (await IsPartialRemainderPaymentAsync(payment, ct))
        {
            return Conflict(new { message = PaymentRemainderTransferRules.RemainderDeleteBlockedMessage });
        }

        var transferredRemainder = await FindPartialRemainderPaymentAsync(payment.Id, ct);
        if (transferredRemainder is not null)
        {
            return Conflict(new { message = PaymentRemainderTransferRules.SourceDeleteBlockedMessage });
        }

        if (
            InvoiceManagedPaymentRules.IsInvoiceManagedPayment(payment)
            && (
                payment.DocumentKind == DocumentKind.Invoice
                || InvoiceManagedPaymentRules.IsGeneratedInvoiceRemainderPayment(payment)
            )
        )
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.DeleteBlockedMessage });
        }

        if (await _db.ClientBalanceTransactions.AsNoTracking().AnyAsync(item => item.PaymentId == payment.Id, ct))
            return BadRequest(new { message = "По платежу есть история баланса. Удаление скрыло бы её основание; требуется сверка истории." });

        var invoiceId = payment.InvoiceId;
        _db.Payments.Remove(payment);
        await _db.SaveChangesAsync(ct);
        if (invoiceId.HasValue && invoiceId.Value > 0)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, invoiceId.Value, DateTime.UtcNow, ct);
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}
