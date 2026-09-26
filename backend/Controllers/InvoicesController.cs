using System.Data;
using System.Linq.Expressions;
using System.Globalization;
using System.Text;
using System.Net;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InvoicesController : ControllerBase
{
    private const string InvoiceNumberStartSettingKey = "invoices.numbering.startNumber";
    private const string InvoiceNumberStrategySettingKey = "invoices.numbering.strategy";
    private const string InvoiceMaxClientCasesSettingKey = "invoices.rules.maxClientCasesPerInvoice";
    private const string InvoiceMaxActsSettingKey = "invoices.rules.maxActsPerInvoice";
    private const string InvoiceDocumentTemplatesSettingKey = "invoices.documentTemplates";
    private const int InvoiceDocumentTemplateHistoryObjectId = 1;
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string DefaultMainCompanyIdSettingKey = "legalEntities.defaultMainCompanyId";
    private readonly PaymentContext _db;
    private readonly IEmailService _emailService;
    private readonly IFormRequirementsService _formRequirements;
    private readonly IInvoiceDocumentPdfService? _invoiceDocumentPdfService;
    private readonly IYandexDiskService? _yandexDiskService;
    private readonly INotificationService? _notificationService;
    private const string InstallmentPaymentMarker = "[pp:installment-payment]";
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly JsonSerializerOptions InstallmentMetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public InvoicesController(
        PaymentContext db,
        IEmailService emailService,
        IFormRequirementsService formRequirements,
        IInvoiceDocumentPdfService? invoiceDocumentPdfService = null,
        IYandexDiskService? yandexDiskService = null,
        INotificationService? notificationService = null)
    {
        _db = db;
        _emailService = emailService;
        _formRequirements = formRequirements;
        _invoiceDocumentPdfService = invoiceDocumentPdfService;
        _yandexDiskService = yandexDiskService;
        _notificationService = notificationService;
    }

    [HttpGet("reference-dictionaries")]
    [RequireSectionPermission(RolePermissionCatalog.Accounts)]
    public async Task<ActionResult<InvoiceReferenceDictionariesDto>> GetReferenceDictionaries(CancellationToken ct)
    {
        var invoiceUnits = await _db.InvoiceUnits
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync(ct);
        var services = await _db.Services
            .AsNoTracking()
            .Include(item => item.Category)
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync(ct);
        var serviceCategories = await _db.ServiceCategories
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync(ct);

        return Ok(new InvoiceReferenceDictionariesDto
        {
            InvoiceUnits = invoiceUnits,
            Services = services,
            ServiceCategories = serviceCategories,
        });
    }

    private Task<TimeZoneInfo> ResolveInvoiceTimeZoneAsync(CancellationToken ct)
        => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);

    private async Task<DateTime> ResolveInvoiceTodayAsync(CancellationToken ct)
    {
        var timeZone = await ResolveInvoiceTimeZoneAsync(ct);
        return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
    }

    private async Task<ActionResult?> RequireAccountsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Accounts, isAllowed, ct)
            ? null
            : Forbid();

    private ActionResult MissingAccountsVisibilityResult()
        => User.GetUserId().HasValue ? Forbid() : Unauthorized();

    private async Task<bool> IsCurrentUserAdminAsync(CancellationToken ct)
        => await _db.IsUserAdminOrLegacyFallbackAsync(User, ct);

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

    private async Task NotifyInvoiceCreatedAsync(Payment invoicePayment, CancellationToken ct)
    {
        if (_notificationService is null)
        {
            return;
        }

        await _db.Entry(invoicePayment).Collection(item => item.Responsibles).LoadAsync(ct);
        await _db.Entry(invoicePayment).Reference(item => item.Invoice).LoadAsync(ct);
        var recipientUserIds = await ResolveInvoiceNotificationCandidatesAsync(
            invoicePayment.Responsibles.Select(item => item.UserId),
            ct);
        if (recipientUserIds.Length == 0)
        {
            return;
        }

        await _notificationService.NotifyInvoiceCreatedAsync(
            invoicePayment,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipientUserIds,
            ct);
    }

    private async Task NotifyInvoiceChangedAsync(
        Payment invoicePayment,
        IReadOnlyList<string> changeLabels,
        CancellationToken ct)
    {
        if (_notificationService is null || changeLabels.Count == 0)
        {
            return;
        }

        await _db.Entry(invoicePayment).Collection(item => item.Responsibles).LoadAsync(ct);
        await _db.Entry(invoicePayment).Reference(item => item.Invoice).LoadAsync(ct);
        var recipientUserIds = await ResolveInvoiceNotificationCandidatesAsync(
            invoicePayment.Responsibles.Select(item => item.UserId),
            ct);
        if (recipientUserIds.Length == 0)
        {
            return;
        }

        await _notificationService.NotifyInvoiceChangedAsync(
            invoicePayment,
            changeLabels,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipientUserIds,
            ct);
    }

    private static IReadOnlyList<string> BuildInvoiceStatusChangeLabels(
        PaymentStatus originalStatus,
        PaymentStatus currentStatus,
        string? originalWorkflowStatus,
        string? currentWorkflowStatus)
    {
        var labels = new List<string>();
        if (originalStatus != currentStatus)
        {
            labels.Add($"Статус оплаты: {originalStatus.ToDisplayString()} -> {currentStatus.ToDisplayString()}");
        }

        var originalWorkflow = InvoiceWorkflowStatus.Normalize(originalWorkflowStatus) ?? originalWorkflowStatus ?? string.Empty;
        var currentWorkflow = InvoiceWorkflowStatus.Normalize(currentWorkflowStatus) ?? currentWorkflowStatus ?? string.Empty;
        if (!string.Equals(originalWorkflow, currentWorkflow, StringComparison.Ordinal))
        {
            labels.Add($"Статус счета: {FormatInvoiceWorkflowStatus(originalWorkflow)} -> {FormatInvoiceWorkflowStatus(currentWorkflow)}");
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

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] string? clientIds,
        [FromQuery] int? contractId,
        [FromQuery] int? caseId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        [FromQuery] PaymentType? type,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] string? clientRole = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default,
        [FromQuery] string? workflowStatus = null,
        [FromQuery] int? actId = null,
        [FromQuery] int? paymentId = null,
        [FromQuery] bool installmentsOnly = false)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        var effectiveResponsibleId = ResolveVisibleResponsibleId(responsibleId, visibility);

        var normalizedWorkflowStatus = NormalizeInvoiceWorkflowStatusOrNull(workflowStatus);
        if (!string.IsNullOrWhiteSpace(workflowStatus) && normalizedWorkflowStatus is null)
        {
            return BadRequest("Unknown invoice workflow status.");
        }

        if (status.HasValue)
        {
            var requiresMetadataBeforePagination = string.Equals(sortBy, "responsible", StringComparison.OrdinalIgnoreCase);
            var statusItems = await QueryInvoiceDtosAsync(
                from,
                to,
                clientId,
                legalEntityId,
                ParseIntList(clientIds),
                contractId,
                actId,
                paymentId,
                caseId,
                effectiveResponsibleId,
                search,
                type,
                normalizedWorkflowStatus,
                clientRole,
                includeMetadata: requiresMetadataBeforePagination,
                ct,
                visibility,
                installmentsOnly);

            statusItems = statusItems
                .Where(item => item.Status == status.Value)
                .ToList();

            var orderedStatusItems = ApplyInvoiceSort(statusItems, sortBy, sortDir).ToList();
            var statusTotal = orderedStatusItems.Count;
            var statusTotalPages = Math.Max(1, (int)Math.Ceiling(statusTotal / (double)pageSize));
            if (page > statusTotalPages)
            {
                page = statusTotalPages;
            }

            var statusPageItems = orderedStatusItems
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
            if (!requiresMetadataBeforePagination)
            {
                await EnrichInvoiceMetadataAsync(statusPageItems, ct);
            }

            return Ok(new { items = statusPageItems, total = statusTotal, page, pageSize });
        }

        var query = BuildInvoiceQuery(visibility);
        query = ApplyFilters(
            query,
            from,
            to,
            null,
            clientId,
            legalEntityId,
            contractId,
            caseId,
            effectiveResponsibleId,
            search,
            type,
            clientRole,
            ParseIntList(clientIds));
        query = ApplyRelatedDocumentFilters(query, actId, paymentId);
        query = ApplyInstallmentsFilter(query, installmentsOnly);

        if (normalizedWorkflowStatus is not null)
        {
            query = query.Where(x => x.Payment.InvoiceWorkflowStatus == normalizedWorkflowStatus);
        }

        var total = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        if (page > totalPages)
        {
            page = totalPages;
        }

        var rawPageItems = await ApplySort(query, sortBy, sortDir)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ProjectionRaw)
            .ToListAsync(ct);

        var pageItems = rawPageItems
            .Select(MapFromRaw)
            .ToList();
        await EnrichInvoiceDtosAsync(pageItems, ct);

        return Ok(new { items = pageItems, total, page, pageSize });
    }

    [HttpGet("summary")]
    public async Task<ActionResult<InvoiceSummaryDto>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] int? contractId = null,
        [FromQuery] int? caseId = null,
        [FromQuery] int? responsibleId = null,
        [FromQuery] string? search = null,
        [FromQuery] PaymentType? type = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] int? legalEntityId = null,
        CancellationToken ct = default,
        [FromQuery] string? workflowStatus = null,
        [FromQuery] int? actId = null,
        [FromQuery] int? paymentId = null,
        [FromQuery] bool installmentsOnly = false)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        var effectiveResponsibleId = ResolveVisibleResponsibleId(responsibleId, visibility);

        var normalizedWorkflowStatus = NormalizeInvoiceWorkflowStatusOrNull(workflowStatus);
        if (!string.IsNullOrWhiteSpace(workflowStatus) && normalizedWorkflowStatus is null)
        {
            return BadRequest("Unknown invoice workflow status.");
        }

        var today = await ResolveInvoiceTodayAsync(ct);
        var items = await QueryInvoiceDtosAsync(
            from,
            to,
            clientId,
            legalEntityId,
            clientIds: null,
            contractId,
            actId,
            paymentId,
            caseId,
            effectiveResponsibleId,
            search,
            type,
            normalizedWorkflowStatus,
            clientRole,
            includeMetadata: false,
            ct,
            visibility,
            installmentsOnly);

        if (status.HasValue)
        {
            items = items
                .Where(item => item.Status == status.Value)
                .ToList();
        }

        decimal overdue0To10 = 0m;
        decimal overdue11To30 = 0m;
        decimal overdue31To60 = 0m;
        decimal overdue61To90 = 0m;
        decimal overdue91Plus = 0m;

        foreach (var row in items.Where(item => item.Status == PaymentStatus.Overdue && item.DueDate.HasValue))
        {
            var daysOverdue = Math.Max(0, (today - row.DueDate!.Value.Date).Days);
            var outstandingAmount = ResolveOutstandingInvoiceSummaryAmount(row);
            if (daysOverdue <= 10)
            {
                overdue0To10 += outstandingAmount;
                continue;
            }

            if (daysOverdue <= 30)
            {
                overdue11To30 += outstandingAmount;
                continue;
            }

            if (daysOverdue <= 60)
            {
                overdue31To60 += outstandingAmount;
                continue;
            }

            if (daysOverdue <= 90)
            {
                overdue61To90 += outstandingAmount;
                continue;
            }

            overdue91Plus += outstandingAmount;
        }

        var includeCancelledInTotal = status == PaymentStatus.Cancelled;
        var totalItems = items.Where(item => includeCancelledInTotal || item.Status != PaymentStatus.Cancelled).ToList();
        decimal totalAmount = totalItems.Sum(item => item.Amount);
        int totalCount = totalItems.Count;

        var pendingItems = items
            .Where(item => item.Status is PaymentStatus.Pending or PaymentStatus.Processing)
            .ToList();
        var paidItems = items.Where(item => item.Status == PaymentStatus.Completed).ToList();
        var overdueItems = items.Where(item => item.Status == PaymentStatus.Overdue).ToList();

        var summary = new InvoiceSummaryDto
        {
            Total = new InvoiceSummaryBucketDto { Amount = totalAmount, Count = totalCount },
            Pending = new InvoiceSummaryBucketDto
            {
                Amount = pendingItems.Sum(ResolveOutstandingInvoiceSummaryAmount),
                Count = pendingItems.Count,
            },
            Paid = new InvoiceSummaryBucketDto
            {
                Amount = paidItems.Sum(ResolvePaidInvoiceSummaryAmount),
                Count = paidItems.Count,
            },
            Overdue = new InvoiceSummaryBucketDto
            {
                Amount = overdueItems.Sum(ResolveOutstandingInvoiceSummaryAmount),
                Count = overdueItems.Count,
            },
            OverdueAging = new InvoiceOverdueAgingDto
            {
                Day0To10 = overdue0To10,
                Day11To30 = overdue11To30,
                Day31To60 = overdue31To60,
                Day61To90 = overdue61To90,
                Day91Plus = overdue91Plus,
                Total = overdue0To10 + overdue11To30 + overdue31To60 + overdue61To90 + overdue91Plus,
            },
        };

        return Ok(summary);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> GetById(int id, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }

        var raw = await BuildInvoiceQuery(visibility)
            .Where(x => x.Payment.Id == id)
            .Select(ProjectionRaw)
            .FirstOrDefaultAsync(ct);

        if (raw is null)
        {
            return NotFound();
        }

        var dto = MapFromRaw(raw);
        await EnrichInvoiceDtosAsync(new List<InvoiceDto> { dto }, ct);
        return Ok(dto);
    }

    [HttpPost("repair-payment-state")]
    [HttpPost("recalculate-payment-state")]
    public async Task<ActionResult<RepairInvoicePaymentStateResponse>> RepairPaymentState(
        [FromBody] RepairInvoicePaymentStateRequest? request,
        CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Unauthorized();
        }

        request ??= new RepairInvoicePaymentStateRequest();
        var dryRun = request.DryRun;
        var requestedLimit = request.Limit.GetValueOrDefault(5_000);
        var limit = Math.Clamp(requestedLimit <= 0 ? 5_000 : requestedLimit, 1, 50_000);
        var invoiceIds = request.InvoiceIds?
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet()
            ?? new HashSet<int>();
        var paymentIds = request.PaymentIds?
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet()
            ?? new HashSet<int>();
        var hasInvoiceFilter = invoiceIds.Count > 0;
        var hasPaymentFilter = paymentIds.Count > 0;

        var candidateQuery = _db.Payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice);

        if (hasInvoiceFilter || hasPaymentFilter)
        {
            candidateQuery = candidateQuery.Where(payment =>
                (hasInvoiceFilter
                    && ((payment.InvoiceId.HasValue && invoiceIds.Contains(payment.InvoiceId.Value))
                        || invoiceIds.Contains(payment.Id)))
                || (hasPaymentFilter && paymentIds.Contains(payment.Id)));
        }

        var candidates = await candidateQuery
            .OrderBy(payment => payment.InvoiceId ?? int.MaxValue)
            .ThenBy(payment => payment.Id)
            .Select(payment => new { payment.Id, payment.InvoiceId })
            .Take(limit)
            .ToListAsync(ct);

        var invoiceEntityIds = candidates
            .Where(candidate => candidate.InvoiceId.HasValue && candidate.InvoiceId.Value > 0)
            .Select(candidate => candidate.InvoiceId!.Value)
            .Distinct()
            .ToList();
        var standalonePaymentIds = candidates
            .Where(candidate => !candidate.InvoiceId.HasValue || candidate.InvoiceId.Value <= 0)
            .Select(candidate => candidate.Id)
            .Distinct()
            .ToList();

        var aggregatePayments = invoiceEntityIds.Count == 0
            ? new List<Payment>()
            : await _db.Payments
                .Where(payment => payment.InvoiceId.HasValue && invoiceEntityIds.Contains(payment.InvoiceId.Value))
                .ToListAsync(ct);
        var standalonePayments = standalonePaymentIds.Count == 0
            ? new List<Payment>()
            : await _db.Payments
                .Where(payment => standalonePaymentIds.Contains(payment.Id))
                .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var scanned = 0;
        var applied = 0;
        var items = new List<RepairInvoicePaymentStateItemDto>();

        foreach (var group in aggregatePayments
            .GroupBy(payment => payment.InvoiceId!.Value)
            .OrderBy(group => group.Key))
        {
            ApplyEvaluation(group.ToList());
        }

        foreach (var standalonePayment in standalonePayments.OrderBy(payment => payment.Id))
        {
            ApplyEvaluation(new List<Payment> { standalonePayment });
        }

        if (!dryRun && applied > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new RepairInvoicePaymentStateResponse
        {
            DryRun = dryRun,
            Scanned = scanned,
            Changed = items.Count,
            Applied = applied,
            Items = items,
        });

        void ApplyEvaluation(List<Payment> payments)
        {
            var evaluation = InvoicePaymentStateService.EvaluateInvoiceAggregate(payments, now);
            if (evaluation is null)
            {
                return;
            }

            scanned++;
            if (!evaluation.HasChanges)
            {
                return;
            }

            items.Add(new RepairInvoicePaymentStateItemDto
            {
                InvoiceId = evaluation.InvoiceId,
                RepresentativePaymentId = evaluation.RepresentativePaymentId,
                Account = evaluation.Account,
                AccountDate = evaluation.AccountDate,
                SourcePaymentCount = evaluation.SourcePaymentCount,
                Current = evaluation.Current,
                Suggested = evaluation.Suggested,
            });

            if (dryRun)
            {
                return;
            }

            var representative = payments.First(payment => payment.Id == evaluation.RepresentativePaymentId);
            if (InvoicePaymentStateService.ApplySnapshot(representative, evaluation.Suggested))
            {
                applied++;
            }
        }
    }

    [HttpPost("backfill-expense-cases")]
    public async Task<ActionResult<BackfillExpenseInvoiceCasesResponse>> BackfillExpenseInvoiceCases(
        [FromBody] BackfillExpenseInvoiceCasesRequest? request,
        CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        request ??= new BackfillExpenseInvoiceCasesRequest();
        if (!request.DryRun && !request.Confirm)
        {
            return BadRequest(new { message = "Backfill requires confirm=true when dryRun=false." });
        }

        var limit = Math.Clamp(request.Limit.GetValueOrDefault(1_000), 1, 50_000);
        var result = await ExpenseInvoiceCaseBackfillService.RunAsync(
            _db,
            request.DryRun,
            limit,
            request.PaymentIds,
            ct);

        return Ok(result);
    }

    [HttpPost("{id:int}/send-to-signing")]
    public async Task<ActionResult<InvoiceDto>> SendToSigning(int id, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var payment = await BuildEditableInvoicePaymentsQuery(visibility).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var originalStatus = payment.Status;
        var originalWorkflowStatus = payment.InvoiceWorkflowStatus;
        ApplyInvoiceWorkflowStatus(payment, InvoiceWorkflowStatus.OnSigning, now, reason: null);
        await SyncSiblingInvoiceWorkflowStatusesAsync(payment, InvoiceWorkflowStatus.OnSigning, now, reason: null, ct);

        var dto = await SaveAndReloadInvoiceAsync(payment, ct);
        await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);
        return Ok(dto);
    }

    [HttpPost("{id:int}/mark-signed")]
    public async Task<ActionResult<InvoiceDto>> MarkSigned(int id, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var payment = await BuildEditableInvoicePaymentsQuery(visibility).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var originalStatus = payment.Status;
        var originalWorkflowStatus = payment.InvoiceWorkflowStatus;
        ApplyInvoiceWorkflowStatus(payment, InvoiceWorkflowStatus.SignedByClient, now, reason: null);
        await SyncSiblingInvoiceWorkflowStatusesAsync(payment, InvoiceWorkflowStatus.SignedByClient, now, reason: null, ct);
        var dto = await SaveAndReloadInvoiceAsync(payment, ct);
        await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);
        return Ok(dto);
    }

    [HttpPost("{id:int}/mark-not-signed")]
    public async Task<ActionResult<InvoiceDto>> MarkNotSigned(int id, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var payment = await BuildEditableInvoicePaymentsQuery(visibility).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var originalStatus = payment.Status;
        var originalWorkflowStatus = payment.InvoiceWorkflowStatus;
        ApplyInvoiceWorkflowStatus(payment, InvoiceWorkflowStatus.NotSigned, now, reason: null);
        await SyncSiblingInvoiceWorkflowStatusesAsync(payment, InvoiceWorkflowStatus.NotSigned, now, reason: null, ct);
        var dto = await SaveAndReloadInvoiceAsync(payment, ct);
        await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);
        return Ok(dto);
    }

    [HttpPost("{id:int}/workflow-status")]
    public async Task<ActionResult<InvoiceDto>> UpdateWorkflowStatus(int id, [FromBody] UpdateNamedStatusRequest request, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var workflowStatus = NormalizeInvoiceWorkflowStatusOrNull(request.Status);
        if (workflowStatus is null)
        {
            return BadRequest("Unknown invoice workflow status.");
        }
        if (!IsEditableInvoiceWorkflowStatus(workflowStatus))
        {
            return BadRequest("Only document signing workflow statuses can be changed through this endpoint.");
        }
        if (workflowStatus == InvoiceWorkflowStatus.Cancelled)
        {
            return BadRequest("Аннулируйте счёт через действие «Отмена счёта» с указанием причины и порядка обработки оплаты.");
        }

        var payment = await BuildEditableInvoicePaymentsQuery(visibility).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        if (payment.DocumentKind != DocumentKind.Invoice)
        {
            return BadRequest("Only invoice documents can change workflow status through this endpoint.");
        }
        if (payment.Status == PaymentStatus.Cancelled || payment.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled)
        {
            return BadRequest("Аннулированный счёт нельзя вернуть в работу сменой статуса.");
        }

        var reason = Normalize(request.Reason);
        var statusChanged = !string.Equals(payment.InvoiceWorkflowStatus, workflowStatus, StringComparison.OrdinalIgnoreCase);
        var originalStatus = payment.Status;
        var originalWorkflowStatus = payment.InvoiceWorkflowStatus;
        if (statusChanged)
        {
            var reasonError = await ValidateFinancialActionReasonAsync(reason, "изменения статуса документа", ct);
            if (reasonError is not null)
            {
                return reasonError;
            }

            ApplyAuditReason(reason);
        }

        var now = DateTime.UtcNow;
        payment.InvoiceWorkflowStatus = workflowStatus;
        ApplyInvoiceWorkflowStatus(payment, workflowStatus, now, reason);
        await SyncSiblingInvoiceWorkflowStatusesAsync(payment, workflowStatus, now, reason, ct);

        switch (workflowStatus)
        {
            case InvoiceWorkflowStatus.Draft:
            case InvoiceWorkflowStatus.NotSigned:
            case InvoiceWorkflowStatus.RequiresClientSignature:
            case InvoiceWorkflowStatus.OnApproval:
            case InvoiceWorkflowStatus.Approved:
            case InvoiceWorkflowStatus.ApprovalRejected:
            case InvoiceWorkflowStatus.NeedsCorrection:
                payment.InvoiceSentForSigningAt = null;
                payment.InvoiceSignedAt = null;
                payment.InvoiceCancelledAt = null;
                payment.InvoiceCancelledByUserId = null;
                payment.InvoiceCancelledReason = null;
                break;
            case InvoiceWorkflowStatus.OnSigning:
                payment.InvoiceSentForSigningAt ??= now;
                payment.InvoiceSignedAt = null;
                payment.InvoiceCancelledAt = null;
                payment.InvoiceCancelledByUserId = null;
                payment.InvoiceCancelledReason = null;
                break;
            case InvoiceWorkflowStatus.SignedByClient:
                payment.InvoiceSentForSigningAt ??= now;
                payment.InvoiceSignedAt ??= now;
                payment.InvoiceCancelledAt = null;
                payment.InvoiceCancelledByUserId = null;
                payment.InvoiceCancelledReason = null;
                break;
            case InvoiceWorkflowStatus.Cancelled:
                payment.InvoiceCancelledAt ??= now;
                payment.InvoiceCancelledByUserId ??= User.GetUserId();
                payment.InvoiceCancelledReason ??= reason ?? "Быстрая смена статуса документа";
                break;
        }

        var dto = await SaveAndReloadInvoiceAsync(payment, ct);
        if (statusChanged)
        {
            await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);
        }
        return Ok(dto);
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<InvoiceDto>> Cancel(int id, [FromBody] CancelInvoiceRequest request, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var reason = Normalize(request.Reason);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return BadRequest("Cancellation reason is required.");
        }
        ApplyAuditReason(reason);

        var payment = await BuildEditableInvoicePaymentsQuery(visibility)
            .Include(p => p.Invoice)
            .Include(p => p.Client)
            .Include(p => p.LegalEntity)
            .Include(p => p.PaymentSource)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        if (payment.DocumentKind != DocumentKind.Invoice)
        {
            return BadRequest("Only invoice documents can be cancelled through this endpoint.");
        }

        var invoiceEntityId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (!invoiceEntityId.HasValue || invoiceEntityId.Value <= 0)
        {
            return BadRequest("Invoice aggregate was not found.");
        }

        if (!Enum.IsDefined(request.CancellationScope))
        {
            return BadRequest("Unknown invoice cancellation scope.");
        }

        var originalStatus = payment.Status;
        var originalWorkflowStatus = payment.InvoiceWorkflowStatus;
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockInvoiceAggregateAsync(invoiceEntityId.Value, ct);

        var generatedPayments = await LoadGeneratedInstallmentPaymentsAsync(invoiceEntityId.Value, payment.Id, ct);
        var now = DateTime.UtcNow;
        var actor = User.GetDisplayName();
        var actorId = GetUserId(User);
        var invoiceTimeZone = await ResolveInvoiceTimeZoneAsync(ct);

        if (request.CancellationScope == InvoiceCancellationScope.UnpaidInstallmentsOnly)
        {
            var unpaidInstallmentCancelError = CancelUnpaidGeneratedInstallmentPayments(
                payment,
                generatedPayments,
                reason,
                now,
                invoiceTimeZone);
            if (unpaidInstallmentCancelError is not null)
            {
                return unpaidInstallmentCancelError;
            }

            await AppendInvoiceCancellationCaseEventsAsync(
                payment,
                $"По счёту {BuildInvoicePaymentReference(payment)} отменены только неоплаченные платежи графика рассрочки. Оплаченная часть сохранена. Причина: {reason}. Инициатор: {actor}.",
                now,
                invoiceTimeZone,
                ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var updatedDto = await LoadInvoiceDtoAsync(payment.Id, ct);
            await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);
            return Ok(updatedDto);
        }

        var paymentsToCancel = generatedPayments
            .Where(item => item.Status != PaymentStatus.Cancelled)
            .Prepend(payment)
            .ToList();
        var receivedAmount = NormalizeMoneyAmount(paymentsToCancel.Sum(ResolvePaidAmount));
        var hasReceivedMoney = paymentsToCancel.Any(HasReceivedMoney);

        if (hasReceivedMoney)
        {
            if (request.ReceivedMoneyAction is not InvoiceCancellationMoneyAction.LeaveAsAdvance
                and not InvoiceCancellationMoneyAction.Reallocate
                and not InvoiceCancellationMoneyAction.Refund)
            {
                return BadRequest("Нельзя аннулировать счёт с уже полученной оплатой без сценария возврата, зачёта или переноса в аванс.");
            }

            if (payment.Type != PaymentType.Income || !HasSingleCounterpartyOwner(payment.ClientId, payment.LegalEntityId))
            {
                return BadRequest("Обработать оплату можно только по входящему счёту с указанным клиентом или юридическим лицом.");
            }

            if (receivedAmount <= 0m)
            {
                return BadRequest("По счёту отмечена оплата, но сумма полученных денег не определена.");
            }
        }

        Payment? reallocationTarget = null;
        if (hasReceivedMoney && request.ReceivedMoneyAction == InvoiceCancellationMoneyAction.Reallocate)
        {
            if (!request.TargetPaymentId.HasValue || request.TargetPaymentId.Value <= 0)
            {
                return BadRequest("Для зачёта оплаты укажите целевой счёт.");
            }

            if (request.TargetPaymentId.Value == payment.Id)
            {
                return BadRequest("Нельзя зачесть оплату на тот же счёт, который аннулируется.");
            }

            reallocationTarget = await _db.Payments
                .FirstOrDefaultAsync(item => item.Id == request.TargetPaymentId.Value, ct);
            if (reallocationTarget is null)
            {
                return BadRequest("Целевой счёт для зачёта не найден.");
            }

            if (reallocationTarget.DocumentKind != DocumentKind.Invoice || reallocationTarget.Type != PaymentType.Income)
            {
                return BadRequest("Зачесть оплату можно только на другой входящий счёт.");
            }

            if (reallocationTarget.ClientId != payment.ClientId || reallocationTarget.LegalEntityId != payment.LegalEntityId)
            {
                return BadRequest("Зачесть оплату можно только на счёт того же контрагента.");
            }

            if (reallocationTarget.Status == PaymentStatus.Cancelled)
            {
                return BadRequest("Нельзя зачесть оплату на аннулированный счёт.");
            }

            var targetInvoiceEntityId = reallocationTarget.InvoiceId;
            if (targetInvoiceEntityId.HasValue && targetInvoiceEntityId.Value > 0)
            {
                await LockInvoiceAggregateAsync(targetInvoiceEntityId.Value, ct);
            }

            var targetOutstandingAmount = ResolveOutstandingAmount(reallocationTarget);
            if (targetOutstandingAmount <= 0m)
            {
                return BadRequest("По целевому счёту нет остатка для зачёта.");
            }
        }

        var installmentCancelError = CancelGeneratedInstallmentPayments(
            generatedPayments,
            reason,
            allowReceivedMoney: hasReceivedMoney,
            now: now,
            invoiceTimeZone: invoiceTimeZone);
        if (installmentCancelError is not null)
        {
            return installmentCancelError;
        }

        await CancelLinkedActsAsync(payment, reason, now, actorId, ct);

        if (hasReceivedMoney && request.ReceivedMoneyAction is InvoiceCancellationMoneyAction.LeaveAsAdvance or InvoiceCancellationMoneyAction.Reallocate)
        {
            await ClientBalanceService.UpsertCancelledInvoiceAdvanceAsync(
                _db,
                payment,
                receivedAmount,
                reason,
                now,
                HttpContext,
                ct);
        }

        MoneyOperation? refundOperation = null;
        if (hasReceivedMoney && request.ReceivedMoneyAction == InvoiceCancellationMoneyAction.Refund)
        {
            refundOperation = await CreateInvoiceRefundOperationAsync(
                payment,
                paymentsToCancel,
                invoiceEntityId.Value,
                receivedAmount,
                reason,
                request,
                now,
                ct);
        }

        decimal reallocatedAmount = 0m;
        if (reallocationTarget is not null)
        {
            await _db.SaveChangesAsync(ct);
            var reallocationDescription = BuildCancelledInvoiceReallocationDescription(payment, reallocationTarget, reason);
            var reallocationResult = await ClientBalanceService.ApplyCounterpartyCreditAsync(
                _db,
                payment.ClientId,
                payment.LegalEntityId,
                reallocationTarget.Id,
                receivedAmount,
                now,
                HttpContext,
                ct,
                description: reallocationDescription,
                externalKey: ClientBalanceService.BuildCancelledInvoiceReallocationKey(payment.Id, reallocationTarget.Id));

            if (!reallocationResult.Success)
            {
                return BadRequest(reallocationResult.Error ?? "Не удалось зачесть оплату на целевой счёт.");
            }

            reallocatedAmount = reallocationResult.AppliedAmount;
            PaymentDomainService.AppendSystemNote(
                payment,
                $"Оплата зачтена на счёт #{reallocationTarget.Id}: {reallocatedAmount:N2}",
                now,
                actor,
                invoiceTimeZone);
            PaymentDomainService.AppendSystemNote(
                reallocationTarget,
                $"Зачтена оплата по аннулированному счёту #{payment.Id}: {reallocatedAmount:N2}",
                now,
                actor,
                invoiceTimeZone);
            await AppendInvoiceCancellationCaseEventsAsync(
                reallocationTarget,
                $"В счёт {BuildInvoicePaymentReference(reallocationTarget)} зачтена оплата по аннулированному счёту {BuildInvoicePaymentReference(payment)}: {reallocatedAmount:N2}. Причина: {reason}. Инициатор: {actor}.",
                now,
                invoiceTimeZone,
                ct);
        }

        payment.Status = PaymentStatus.Cancelled;
        payment.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled;
        payment.InvoiceCancelledAt ??= now;
        payment.InvoiceCancelledByUserId ??= actorId;
        payment.InvoiceCancelledReason = reason;
        payment.PaidAmount = 0m;
        payment.IsPaid = false;
        payment.PaidDate = null;
        payment.LastPaymentDate = null;

        await ReleaseCancelledInvoiceBillingTasksAsync(payment, reason, now, actorId, actor, ct);

        var advanceNote = refundOperation is not null
            ? $" Полученная оплата возвращена клиенту денежной операцией #{refundOperation.Id}: {receivedAmount:N2}."
            : hasReceivedMoney && reallocatedAmount > 0m
            ? $" Полученная оплата зачтена на счёт #{reallocationTarget!.Id}: {reallocatedAmount:N2}. Остаток в авансе: {Math.Max(receivedAmount - reallocatedAmount, 0m):N2}."
            : hasReceivedMoney
                ? $" Полученная оплата перенесена в аванс контрагента: {receivedAmount:N2}."
                : string.Empty;
        PaymentDomainService.AppendSystemNote(payment, $"Счёт аннулирован. Причина: {reason}{advanceNote}", now, actor, invoiceTimeZone);
        await AppendInvoiceCancellationCaseEventsAsync(
            payment,
            BuildInvoiceCancellationCaseEvent(payment, reason, receivedAmount, hasReceivedMoney, request.ReceivedMoneyAction, refundOperation, reallocationTarget, reallocatedAmount, actor),
            now,
            invoiceTimeZone,
            ct);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var dto = await LoadInvoiceDtoAsync(payment.Id, ct);
        ApplyPersistedPaymentState(dto, payment);
        await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);
        return Ok(dto);
    }

    private async Task CancelLinkedActsAsync(Payment invoicePayment, string reason, DateTime now, int? actorId, CancellationToken ct)
    {
        var linkedActs = await _db.Acts
            .Where(act => act.InvoiceId == invoicePayment.Id)
            .ToListAsync(ct);

        if (linkedActs.Count == 0)
        {
            return;
        }

        var invoiceNumber = Normalize(invoicePayment.Invoice?.Number) ?? Normalize(invoicePayment.Account) ?? invoicePayment.Id.ToString(CultureInfo.InvariantCulture);
        foreach (var act in linkedActs)
        {
            act.Status = ActStatus.Terminated;
            act.WorkflowStatus = ActWorkflowStatus.Cancelled;
            act.CancelledAt ??= now;
            act.CancelledByUserId ??= actorId;
            act.CancelledReason = reason;
            act.Comment = AppendActCancellationComment(act.Comment, invoiceNumber, reason, now);
            act.UpdatedAt = now;
        }
    }

    private async Task ReleaseCancelledInvoiceBillingTasksAsync(
        Payment invoicePayment,
        string reason,
        DateTime now,
        int? actorId,
        string actor,
        CancellationToken ct)
    {
        var tasks = await _db.TaskJournalItems
            .Where(task => task.BillingInvoiceId == invoicePayment.Id)
            .ToListAsync(ct);
        if (tasks.Count == 0)
        {
            return;
        }

        var number = Normalize(invoicePayment.Invoice?.Number)
            ?? Normalize(invoicePayment.Account)
            ?? invoicePayment.Id.ToString(CultureInfo.InvariantCulture);
        foreach (var task in tasks)
        {
            task.BillingInvoiceId = null;
            task.UpdatedAt = now;
            _db.TaskJournalComments.Add(new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                Kind = "history",
                AuthorUserId = actorId,
                AuthorName = actor,
                Text = $"Счёт №{number} аннулирован. Причина: {reason}. Платная задача снова ожидает выставления счёта.",
                CreatedAt = now,
            });
        }
    }

    private static string AppendActCancellationComment(string? comment, string invoiceNumber, string reason, DateTime now)
    {
        var cancellationLine = $"Акт аннулирован вместе со счётом №{invoiceNumber} {now:dd.MM.yyyy HH:mm}. Причина: {reason}";
        return string.IsNullOrWhiteSpace(comment)
            ? cancellationLine
            : $"{comment.Trim()}{Environment.NewLine}{cancellationLine}";
    }

    private static string BuildCancelledInvoiceReallocationDescription(Payment sourcePayment, Payment targetPayment, string reason)
    {
        var sourceLabel = BuildInvoicePaymentReference(sourcePayment);
        var targetLabel = BuildInvoicePaymentReference(targetPayment);
        return $"Зачет оплаты по аннулированному счёту {sourceLabel} в счёт {targetLabel}. Причина: {reason}";
    }

    private static string BuildInvoicePaymentReference(Payment payment)
        => string.IsNullOrWhiteSpace(payment.Account)
            ? $"#{payment.Id}"
            : $"№{payment.Account.Trim()} (#{payment.Id})";

    private async Task AppendInvoiceCancellationCaseEventsAsync(
        Payment payment,
        string eventText,
        DateTime utcNow,
        TimeZoneInfo timeZone,
        CancellationToken ct)
    {
        var caseIds = new HashSet<int>();
        if (payment.ClientCaseId is > 0)
        {
            caseIds.Add(payment.ClientCaseId.Value);
        }

        var linkedCaseIds = await _db.Set<InvoiceClientCase>()
            .Where(link => link.PaymentId == payment.Id)
            .Select(link => link.ClientCaseId)
            .ToListAsync(ct);
        foreach (var caseId in linkedCaseIds)
        {
            caseIds.Add(caseId);
        }

        if (caseIds.Count == 0)
        {
            return;
        }

        var clientCases = await _db.ClientCases
            .Where(clientCase => caseIds.Contains(clientCase.Id))
            .ToListAsync(ct);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone);
        foreach (var clientCase in clientCases)
        {
            AppendCaseEvent(clientCase, eventText, localNow);
        }
    }

    private static string BuildInvoiceCancellationCaseEvent(
        Payment payment,
        string reason,
        decimal receivedAmount,
        bool hasReceivedMoney,
        InvoiceCancellationMoneyAction moneyAction,
        MoneyOperation? refundOperation,
        Payment? reallocationTarget,
        decimal reallocatedAmount,
        string actor)
    {
        var moneyText = hasReceivedMoney
            ? moneyAction switch
            {
                InvoiceCancellationMoneyAction.Refund when refundOperation is not null
                    => $" Полученная оплата возвращена клиенту денежной операцией #{refundOperation.Id}: {receivedAmount:N2}.",
                InvoiceCancellationMoneyAction.Reallocate when reallocationTarget is not null
                    => $" Полученная оплата зачтена в счёт {BuildInvoicePaymentReference(reallocationTarget)}: {reallocatedAmount:N2}.",
                InvoiceCancellationMoneyAction.LeaveAsAdvance
                    => $" Полученная оплата перенесена в аванс контрагента: {receivedAmount:N2}.",
                _ => string.Empty,
            }
            : string.Empty;

        return $"Счёт {BuildInvoicePaymentReference(payment)} аннулирован. Причина: {reason}.{moneyText} Инициатор: {actor}.";
    }

    private static void AppendCaseEvent(ClientCase clientCase, string eventText, DateTime localNow)
    {
        var line = $"{localNow:dd.MM.yyyy HH:mm}: {eventText}";
        var existing = clientCase.EventHistory?.Trim();
        var next = string.IsNullOrWhiteSpace(existing)
            ? line
            : $"{existing}{Environment.NewLine}{line}";

        clientCase.EventHistory = next.Length <= 4000
            ? next
            : next[^4000..].TrimStart();
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        if (await RequireAccountsPermissionAsync(permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var missingCreate = await _formRequirements.GetMissingFieldsAsync("contract-invoices", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var workflowStatus = ResolveInvoiceWorkflowStatus(request);
        if (workflowStatus is null)
        {
            return BadRequest("Unknown invoice workflow status.");
        }
        if (request.Status == PaymentStatus.Cancelled || workflowStatus == InvoiceWorkflowStatus.Cancelled)
        {
            return BadRequest("Аннулируйте счёт через действие «Отмена счёта» с указанием причины и порядка обработки оплаты.");
        }

        var counterpartyValidationError = await ValidateCounterpartyAsync(request.ClientId, request.LegalEntityId, "counterparty", ct);
        if (counterpartyValidationError is not null) return counterpartyValidationError;

        var expenseClientValidationError = await ValidateExpenseClientAsync(request, ct);
        if (expenseClientValidationError is not null)
        {
            return expenseClientValidationError;
        }

        var contractValidationError = await ValidateContractClientLinkAsync(request.ContractId, request.ClientId, existingInvoiceId: null, ct);
        if (contractValidationError is not null)
        {
            return contractValidationError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        var invoiceDate = PaymentDomainService.NormalizeLocalDate(request.Date);
        number = await ResolveInvoiceNumberForSaveAsync(number, invoiceDate, null, request.AllowDuplicateNumberOnImport, ct);
        if (string.IsNullOrEmpty(number))
        {
            var duplicateNumberError = await EnsureInvoiceNumberIsUniqueAsync(Normalize(request.Number) ?? string.Empty, invoiceDate, null, ct);
            return duplicateNumberError ?? BadRequest("Invoice number is required.");
        }

        if (request.ServiceId.HasValue)
        {
            var serviceExists = await _db.Services.AsNoTracking().AnyAsync(s => s.Id == request.ServiceId.Value, ct);
            if (!serviceExists)
            {
                return BadRequest($"Unknown serviceId {request.ServiceId.Value}");
            }
        }

        var lineItemServiceValidationError = await ValidateInvoiceLineItemServicesAsync(request.LineItems, ct);
        if (lineItemServiceValidationError is not null)
        {
            return lineItemServiceValidationError;
        }

        var caseOwner = ResolveInvoiceCaseOwner(request);
        var clientCaseValidation = await ValidateInvoiceClientCasesAsync(caseOwner.ClientId, caseOwner.LegalEntityId, request, ct);
        if (clientCaseValidation.Error is not null)
        {
            return clientCaseValidation.Error;
        }

        var billingTaskIds = (request.BillingTaskIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var billingTasks = billingTaskIds.Count == 0
            ? []
            : await _db.TaskJournalItems
                .Where(task => billingTaskIds.Contains(task.Id))
                .ToListAsync(ct);
        if (billingTasks.Count != billingTaskIds.Count)
        {
            return BadRequest(new { message = "Одна или несколько платных задач не найдены." });
        }

        if (billingTasks.Any(task =>
                task.ClientBillingClassification != TaskClientBillingClassification.Billable
                || task.BillingModeSnapshot == ContractBillingMode.Fixed
                || task.BillingInvoiceId.HasValue))
        {
            return BadRequest(new { message = "В счёт можно добавить только ещё не выставленные платные задачи по фактической оплате." });
        }

        if (billingTasks.Any(task => task.ClientId != request.ClientId || task.LegalEntityId != request.LegalEntityId))
        {
            return BadRequest(new { message = "Все выбранные задачи должны относиться к контрагенту счёта." });
        }

        if (billingTasks.Any(task => !task.ClientCaseId.HasValue || !clientCaseValidation.ClientCaseIds.Contains(task.ClientCaseId.Value)))
        {
            return BadRequest(new { message = "Добавьте в счёт все дела, к которым относятся выбранные задачи." });
        }

        var actValidation = await ValidateInvoiceActsAsync(request.ClientId, request.LegalEntityId, request, null, null, ct);
        if (actValidation.Error is not null)
        {
            return actValidation.Error;
        }

        var invoice = new Invoice
        {
            Number = number,
            Date = invoiceDate,
            CreatedAt = DateTime.UtcNow,
        };

        var resolvedDueDate = ResolveInvoiceDueDate(request);

        var payment = new Payment
        {
            Account = number,
            DocumentKind = DocumentKind.Invoice,
            AccountDate = invoiceDate,
            Invoice = invoice,
            Date = resolvedDueDate,
            Amount = request.Amount,
            Status = request.Status,
            InvoiceWorkflowStatus = workflowStatus,
            Type = request.Type,
            Description = Normalize(request.Description) ?? string.Empty,
            IsNotForAccounting = request.IsNotForAccounting,
            ClientCaseReference = Normalize(request.ClientCaseReference),
            Notes = Normalize(request.ActReference) ?? string.Empty,
            ClientId = request.ClientId,
            LegalEntityId = request.LegalEntityId,
            ExpenseClientId = ResolveInvoiceExpenseClientId(request),
            ExpenseLegalEntityId = ResolveInvoiceExpenseLegalEntityId(request),
            ContractId = request.ContractId,
            ClientCaseId = clientCaseValidation.ClientCaseIds.Count > 0 ? clientCaseValidation.ClientCaseIds[0] : null,
            PaymentSourceId = request.PaymentSourceId,
            ServiceId = request.ServiceId,
            IncomeTypeId = request.IncomeTypeId,
            DealTypeId = request.DealTypeId,
            PaymentStatusId = request.PaymentStatusEntityId,
            CounterpartyName = null,
            CreatedAt = DateTime.UtcNow,
        };

        ApplyInvoiceWorkflowStatus(payment, workflowStatus, DateTime.UtcNow, reason: null);
        payment.SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes);

        var responsibleIds = await ResolveInvoiceResponsibleIdsAsync(caseOwner.ClientId, caseOwner.LegalEntityId, request.ResponsibleIds, ct);
        if (!await EnsureResponsibleIdsExistAsync(responsibleIds, ct))
        {
            return BadRequest("One or more responsible users were not found.");
        }

        foreach (var responsibleId in responsibleIds)
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }

        ApplyPaidFlags(payment, request.PaidDate, await ResolveInvoiceTodayAsync(ct));

        await using var createTransaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        if (billingTasks.Count > 0)
        {
            var linkedAt = DateTime.UtcNow;
            var linkedByUserId = User.GetUserId();
            var linkedByName = User.GetDisplayName();
            foreach (var task in billingTasks)
            {
                task.BillingInvoiceId = payment.Id;
                task.UpdatedAt = linkedAt;
                task.Comments.Add(new TaskJournalComment
                {
                    Kind = "history",
                    AuthorUserId = linkedByUserId,
                    AuthorName = linkedByName,
                    Text = $"Платная задача включена в счёт №{number}.",
                    CreatedAt = linkedAt,
                });
            }
        }

        var createdInvoiceEntityId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (createdInvoiceEntityId.HasValue && createdInvoiceEntityId.Value > 0)
        {
            await LockInvoiceAggregateAsync(createdInvoiceEntityId.Value, ct);
        }

        await SyncInvoiceLineItemsAsync(payment, request.LineItems, ct);
        await SyncInvoiceClientCasesAsync(payment, clientCaseValidation.ClientCaseIds, ct);
        await SyncInvoiceActsAsync(payment, actValidation.ActIds, number, ct);
        var installmentSyncError = await SyncInstallmentPaymentsAsync(payment, request, responsibleIds, ct);
        if (installmentSyncError is not null)
        {
            return installmentSyncError;
        }
        await _db.SaveChangesAsync(ct);
        await createTransaction.CommitAsync(ct);

        var dto = await LoadInvoiceDtoAsync(payment.Id, ct);
        ApplyPersistedPaymentState(dto, payment);
        await NotifyInvoiceCreatedAsync(payment, ct);

        return Created($"/api/invoices/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> Update(int id, [FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("contract-invoices", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var workflowStatus = ResolveInvoiceWorkflowStatus(request);
        if (workflowStatus is null)
        {
            return BadRequest("Unknown invoice workflow status.");
        }
        if (request.Status == PaymentStatus.Cancelled || workflowStatus == InvoiceWorkflowStatus.Cancelled)
        {
            return BadRequest("Аннулируйте счёт через действие «Отмена счёта» с указанием причины и порядка обработки оплаты.");
        }

        var payment = await BuildEditableInvoicePaymentsQuery(visibility)
            .Include(p => p.Responsibles)
            .Include(p => p.Invoice)
            .Include(p => p.InvoiceClientCases)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        if (payment.Status == PaymentStatus.Cancelled || payment.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled)
        {
            return BadRequest("Аннулированный счёт нельзя изменить или вернуть в работу.");
        }

        var counterpartyValidationError = await ValidateCounterpartyAsync(request.ClientId, request.LegalEntityId, "counterparty", ct);
        if (counterpartyValidationError is not null) return counterpartyValidationError;

        var expenseClientValidationError = await ValidateExpenseClientAsync(request, ct);
        if (expenseClientValidationError is not null)
        {
            return expenseClientValidationError;
        }

        var contractValidationError = await ValidateContractClientLinkAsync(request.ContractId, request.ClientId, id, ct);
        if (contractValidationError is not null)
        {
            return contractValidationError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        var invoiceDate = PaymentDomainService.NormalizeLocalDate(request.Date);
        number = await ResolveInvoiceNumberForSaveAsync(number, invoiceDate, id, request.AllowDuplicateNumberOnImport, ct);
        if (string.IsNullOrEmpty(number))
        {
            var duplicateNumberError = await EnsureInvoiceNumberIsUniqueAsync(Normalize(request.Number) ?? string.Empty, invoiceDate, id, ct);
            return duplicateNumberError ?? BadRequest("Invoice number is required.");
        }

        if (request.ServiceId.HasValue)
        {
            var serviceExists = await _db.Services.AsNoTracking().AnyAsync(s => s.Id == request.ServiceId.Value, ct);
            if (!serviceExists)
            {
                return BadRequest($"Unknown serviceId {request.ServiceId.Value}");
            }
        }

        var lineItemServiceValidationError = await ValidateInvoiceLineItemServicesAsync(request.LineItems, ct);
        if (lineItemServiceValidationError is not null)
        {
            return lineItemServiceValidationError;
        }

        var caseOwner = ResolveInvoiceCaseOwner(request);
        var clientCaseValidation = await ValidateInvoiceClientCasesAsync(caseOwner.ClientId, caseOwner.LegalEntityId, request, ct);
        if (clientCaseValidation.Error is not null)
        {
            return clientCaseValidation.Error;
        }

        var existingInvoiceEntityId = payment.InvoiceId ?? payment.Invoice?.Id;
        var actValidation = await ValidateInvoiceActsAsync(request.ClientId, request.LegalEntityId, request, id, existingInvoiceEntityId, ct);
        if (actValidation.Error is not null)
        {
            return actValidation.Error;
        }

        await using var updateTransaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        if (existingInvoiceEntityId.HasValue && existingInvoiceEntityId.Value > 0)
        {
            await LockInvoiceAggregateAsync(existingInvoiceEntityId.Value, ct);
        }

        var linkedTaskBindings = await _db.TaskJournalItems
            .AsNoTracking()
            .Where(task => task.BillingInvoiceId == payment.Id)
            .Select(task => new { task.ClientId, task.LegalEntityId, task.ClientCaseId })
            .ToListAsync(ct);
        if (linkedTaskBindings.Any(task =>
                task.ClientId != request.ClientId
                || task.LegalEntityId != request.LegalEntityId
                || (clientCaseValidation.ClientCaseIds.Count > 0
                    && (!task.ClientCaseId.HasValue
                        || !clientCaseValidation.ClientCaseIds.Contains(task.ClientCaseId.Value)))))
        {
            return BadRequest("Сначала исправьте связи платных задач: новый контрагент или набор дел счёта не совпадает с ними.");
        }

        var resolvedDueDate = ResolveInvoiceDueDate(request, payment, invoiceDate);
        var resolvedExpenseClientId = ResolveInvoiceExpenseClientId(request);
        var resolvedExpenseLegalEntityId = ResolveInvoiceExpenseLegalEntityId(request);
        int? resolvedClientCaseId = clientCaseValidation.ClientCaseIds.Count > 0
            ? clientCaseValidation.ClientCaseIds[0]
            : null;

        int? preservedProtectedPaymentId = null;
        if (await ShouldForkProtectedRepresentativeInvoicePaymentAsync(
                payment,
                number,
                invoiceDate,
                resolvedDueDate,
                request,
                resolvedExpenseClientId,
                resolvedExpenseLegalEntityId,
                resolvedClientCaseId,
                ct))
        {
            preservedProtectedPaymentId = payment.Id;
            payment = await ForkProtectedRepresentativeInvoicePaymentAsync(payment, ct);
        }

        var invoiceEntityId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (invoiceEntityId.HasValue && invoiceEntityId.Value > 0)
        {
            await LockInvoiceAggregateAsync(invoiceEntityId.Value, ct);
        }

        var previousInvoiceNumber = payment.Account;
        var originalStatus = payment.Status;
        var originalWorkflowStatus = payment.InvoiceWorkflowStatus;
        var actor = User.GetDisplayName();
        var now = DateTime.UtcNow;
        var invoiceTimeZone = await ResolveInvoiceTimeZoneAsync(ct);
        var actionReason = Normalize(request.ActionReason);

        if (originalStatus != request.Status
            || !string.Equals(originalWorkflowStatus, workflowStatus, StringComparison.OrdinalIgnoreCase))
        {
            var reasonError = await ValidateFinancialActionReasonAsync(actionReason, "изменения статуса счета", ct);
            if (reasonError is not null)
            {
                return reasonError;
            }

            ApplyAuditReason(actionReason);
        }

        payment.Account = number;
        payment.DocumentKind = DocumentKind.Invoice;
        payment.AccountDate = invoiceDate;
        payment.Invoice ??= new Invoice
        {
            Number = number,
            Date = invoiceDate,
            CreatedAt = now,
        };
        payment.Invoice.Number = number;
        payment.Invoice.Date = invoiceDate;
        payment.Invoice.UpdatedAt = now;
        payment.InvoiceId = payment.Invoice.Id == 0 ? null : payment.Invoice.Id;
        payment.Date = resolvedDueDate;
        payment.Amount = request.Amount;
        payment.Status = request.Status;
        payment.InvoiceWorkflowStatus = workflowStatus;
        ApplyInvoiceWorkflowStatus(payment, workflowStatus, now, actionReason);
        payment.Type = request.Type;
        payment.Description = Normalize(request.Description) ?? string.Empty;
        payment.IsNotForAccounting = request.IsNotForAccounting;
        payment.ClientCaseReference = Normalize(request.ClientCaseReference);
        payment.Notes = Normalize(request.ActReference) ?? string.Empty;
        payment.ClientId = request.ClientId;
        payment.LegalEntityId = request.LegalEntityId;
        payment.ExpenseClientId = resolvedExpenseClientId;
        payment.ExpenseLegalEntityId = resolvedExpenseLegalEntityId;
        payment.ContractId = request.ContractId;
        payment.ClientCaseId = resolvedClientCaseId;
        payment.PaymentSourceId = request.PaymentSourceId;
        payment.ServiceId = request.ServiceId;
        payment.IncomeTypeId = request.IncomeTypeId;
        payment.DealTypeId = request.DealTypeId;
        payment.PaymentStatusId = request.PaymentStatusEntityId;
        payment.CounterpartyName = null;
        payment.SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes);

        var responsibleIds = await ResolveInvoiceResponsibleIdsAsync(caseOwner.ClientId, caseOwner.LegalEntityId, request.ResponsibleIds, ct);
        if (!await EnsureResponsibleIdsExistAsync(responsibleIds, ct))
        {
            return BadRequest("One or more responsible users were not found.");
        }

        var forRemoval = payment.Responsibles.Where(r => !responsibleIds.Contains(r.UserId)).ToList();
        foreach (var row in forRemoval)
        {
            payment.Responsibles.Remove(row);
        }

        var existingResponsibleIds = payment.Responsibles.Select(r => r.UserId).ToHashSet();
        foreach (var responsibleId in responsibleIds)
        {
            if (existingResponsibleIds.Contains(responsibleId))
            {
                continue;
            }

            payment.Responsibles.Add(new PaymentResponsible
            {
                PaymentId = payment.Id,
                UserId = responsibleId,
            });
        }

        ApplyPaidFlags(payment, request.PaidDate, await ResolveInvoiceTodayAsync(ct));
        await SyncSiblingInvoiceWorkflowStatusesAsync(payment, workflowStatus, now, actionReason, ct);

        if (originalStatus != payment.Status)
        {
            PaymentDomainService.AppendSystemNote(
                payment,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {payment.Status.ToDisplayString()}",
                now,
                actor,
                invoiceTimeZone);
        }

        await SyncInvoiceLineItemsAsync(payment, request.LineItems, ct);
        await SyncInvoiceClientCasesAsync(payment, clientCaseValidation.ClientCaseIds, ct);
        await SyncInvoiceActsAsync(payment, actValidation.ActIds, number, ct, previousInvoiceNumber);
        await SyncLinkedPaymentBindingsAsync(payment, clientCaseValidation.ClientCaseIds, responsibleIds, preservedProtectedPaymentId, ct);
        var installmentSyncError = await SyncInstallmentPaymentsAsync(payment, request, responsibleIds, ct);
        if (installmentSyncError is not null)
        {
            return installmentSyncError;
        }
        await _db.SaveChangesAsync(ct);
        await updateTransaction.CommitAsync(ct);

        var dto = await LoadInvoiceDtoAsync(payment.Id, ct);
        ApplyPersistedPaymentState(dto, payment);
        await NotifyInvoiceChangedAsync(payment, BuildInvoiceStatusChangeLabels(originalStatus, payment.Status, originalWorkflowStatus, payment.InvoiceWorkflowStatus), ct);

        return Ok(dto);
    }

    private async Task<bool> ShouldForkProtectedRepresentativeInvoicePaymentAsync(
        Payment payment,
        string normalizedNumber,
        DateTime invoiceDate,
        DateTime dueDate,
        UpsertInvoiceRequest request,
        int? resolvedExpenseClientId,
        int? resolvedExpenseLegalEntityId,
        int? resolvedClientCaseId,
        CancellationToken ct)
    {
        if (payment.DocumentKind != DocumentKind.Invoice || !HasProtectedInstallmentActivity(payment))
        {
            return false;
        }

        var invoiceEntityId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (!invoiceEntityId.HasValue || invoiceEntityId.Value <= 0)
        {
            return false;
        }

        var siblingPayments = await _db.Payments
            .AsNoTracking()
            .Where(candidate => candidate.InvoiceId == invoiceEntityId.Value && candidate.Id != payment.Id)
            .Select(candidate => new Payment
            {
                Id = candidate.Id,
                DocumentKind = candidate.DocumentKind,
                Amount = candidate.Amount,
                PaidAmount = candidate.PaidAmount,
                Status = candidate.Status,
                IsPaid = candidate.IsPaid,
                Date = candidate.Date,
                PlannedDate = candidate.PlannedDate,
                PaidDate = candidate.PaidDate,
                LastPaymentDate = candidate.LastPaymentDate,
                SystemNotes = candidate.SystemNotes,
                InvoiceWorkflowStatus = candidate.InvoiceWorkflowStatus,
            })
            .ToListAsync(ct);

        if (siblingPayments.Count == 0 || siblingPayments.Any(InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment))
        {
            return false;
        }

        if (IsRepresentativeOfLegacyInvoiceRows(payment, siblingPayments)
            || IsProtectedStateCoveredByRelatedPayments(payment, siblingPayments, request.Amount))
        {
            return false;
        }

        var existingNumber = Normalize(payment.Invoice?.Number) ?? Normalize(payment.Account) ?? string.Empty;
        var existingInvoiceDate = payment.Invoice?.Date.Date
            ?? payment.AccountDate?.Date
            ?? payment.Date.Date;

        if (!string.Equals(existingNumber, normalizedNumber, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (existingInvoiceDate != invoiceDate.Date || payment.Date.Date != dueDate.Date)
        {
            return true;
        }

        if (Math.Abs(payment.Amount - request.Amount) > 0.01m
            || payment.Status != request.Status
            || payment.InvoiceWorkflowStatus != (ResolveInvoiceWorkflowStatus(request) ?? payment.InvoiceWorkflowStatus)
            || payment.Type != request.Type
            || payment.ClientId != request.ClientId
            || payment.LegalEntityId != request.LegalEntityId
            || payment.ExpenseClientId != resolvedExpenseClientId
            || payment.ExpenseLegalEntityId != resolvedExpenseLegalEntityId
            || payment.ContractId != request.ContractId
            || payment.ClientCaseId != resolvedClientCaseId
            || !Nullable.Equals(payment.PaidDate?.Date, request.PaidDate?.Date))
        {
            return true;
        }

        return ShouldReplaceInstallmentPlan(request);
    }

    private static bool IsRepresentativeOfLegacyInvoiceRows(Payment payment, IReadOnlyCollection<Payment> siblingPayments)
    {
        var invoiceRows = siblingPayments
            .Where(candidate => candidate.DocumentKind == DocumentKind.Invoice)
            .Append(payment)
            .ToList();
        if (invoiceRows.Count < 2)
        {
            return false;
        }

        var activeInvoiceRows = invoiceRows
            .Where(candidate => candidate.Status != PaymentStatus.Cancelled)
            .ToList();
        var candidates = activeInvoiceRows.Count > 0 ? activeInvoiceRows : invoiceRows;
        var representative = candidates
            .OrderByDescending(candidate => candidate.Amount)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
        return representative?.Id == payment.Id;
    }

    private static bool IsProtectedStateCoveredByRelatedPayments(
        Payment payment,
        IReadOnlyCollection<Payment> siblingPayments,
        decimal requestedAmount)
    {
        var statusSources = InvoicePaymentStateService.SelectStatusSourcePayments(
            siblingPayments.Append(payment).ToList(),
            payment.Id,
            requestedAmount);
        var paidByRelatedPayments = NormalizeMoneyAmount(statusSources
            .Where(source => source.Id != payment.Id)
            .Sum(InvoicePaymentStateService.ResolvePaidAmount));
        if (paidByRelatedPayments <= 0m)
        {
            return false;
        }

        var requiredPaidAmount = payment.PaidAmount > 0m
            ? Math.Min(payment.PaidAmount, requestedAmount)
            : requestedAmount;
        requiredPaidAmount = NormalizeMoneyAmount(requiredPaidAmount);

        return requiredPaidAmount > 0m && paidByRelatedPayments + 0.01m >= requiredPaidAmount;
    }

    private async Task<Payment> ForkProtectedRepresentativeInvoicePaymentAsync(Payment payment, CancellationToken ct)
    {
        var clonedRepresentative = new Payment
        {
            Account = payment.Account,
            AccountDate = payment.AccountDate,
            InvoiceId = payment.InvoiceId ?? payment.Invoice?.Id,
            LegacyAccountNumber = payment.LegacyAccountNumber,
            LegacyAccountDate = payment.LegacyAccountDate,
            Invoice = payment.Invoice,
            Amount = payment.Amount,
            InvoiceWorkflowStatus = payment.InvoiceWorkflowStatus,
            ClientId = payment.ClientId,
            LegalEntityId = payment.LegalEntityId,
            ExpenseClientId = payment.ExpenseClientId,
            ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
            ContractId = payment.ContractId,
            DocumentKind = DocumentKind.Invoice,
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            DealTypeId = payment.DealTypeId,
            Type = payment.Type,
            Description = payment.Description,
            CounterpartyName = payment.CounterpartyName,
            IncomeTypeId = payment.IncomeTypeId,
            IsPaid = payment.IsPaid,
            PaidAmount = payment.PaidAmount,
            LastPaymentDate = payment.LastPaymentDate,
            Notes = payment.Notes,
            IsNotForAccounting = payment.IsNotForAccounting,
            ClientCaseReference = payment.ClientCaseReference,
            SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes),
            RescheduleCount = payment.RescheduleCount,
            PaidDate = payment.PaidDate,
            PaymentSourceId = payment.PaymentSourceId,
            ServiceId = payment.ServiceId,
            PaymentStatusId = payment.PaymentStatusId,
            ClientCaseId = payment.ClientCaseId,
            CreatedAt = DateTime.UtcNow,
        };

        foreach (var responsibleId in payment.Responsibles.Select(item => item.UserId).Distinct())
        {
            clonedRepresentative.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }

        _db.Payments.Add(clonedRepresentative);
        payment.DocumentKind = DocumentKind.Payment;
        payment.SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes);
        await _db.SaveChangesAsync(ct);

        var lineItemsCollection = _db.Entry(payment).Collection(item => item.InvoiceLineItems);
        if (!lineItemsCollection.IsLoaded)
        {
            await lineItemsCollection.LoadAsync(ct);
        }

        foreach (var lineItem in payment.InvoiceLineItems.ToList())
        {
            payment.InvoiceLineItems.Remove(lineItem);
            clonedRepresentative.InvoiceLineItems.Add(lineItem);
        }
        _db.Entry(clonedRepresentative).Collection(item => item.InvoiceLineItems).IsLoaded = true;

        foreach (var clientCaseLink in payment.InvoiceClientCases.ToList())
        {
            clonedRepresentative.InvoiceClientCases.Add(new InvoiceClientCase
            {
                ClientCaseId = clientCaseLink.ClientCaseId,
            });
            payment.InvoiceClientCases.Remove(clientCaseLink);
            _db.Remove(clientCaseLink);
        }
        _db.Entry(clonedRepresentative).Collection(item => item.InvoiceClientCases).IsLoaded = true;

        var linkedActs = await _db.Acts
            .Where(act => act.InvoiceId == payment.Id)
            .ToListAsync(ct);
        foreach (var act in linkedActs)
        {
            act.InvoiceId = clonedRepresentative.Id;
            act.InvoiceNumber = clonedRepresentative.Account;
        }

        var contractLinks = await _db.ContractInvoices
            .Where(link => link.InvoiceId == payment.Id)
            .ToListAsync(ct);
        foreach (var contractLink in contractLinks)
        {
            contractLink.InvoiceId = clonedRepresentative.Id;
        }

        var linkedTasks = await _db.TaskJournalItems
            .Where(task => task.BillingInvoiceId == payment.Id)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var task in linkedTasks)
        {
            task.BillingInvoiceId = clonedRepresentative.Id;
            task.UpdatedAt = now;
            _db.TaskJournalComments.Add(new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                Kind = "history",
                AuthorUserId = User.GetUserId(),
                AuthorName = User.GetDisplayName(),
                Text = $"Связь со счётом №{clonedRepresentative.Account} сохранена после обновления счёта.",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);

        return clonedRepresentative;
    }

    [HttpGet("numbering-settings")]
    public async Task<ActionResult<object>> GetNumberingSettings(CancellationToken ct)
    {
        var userId = GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var settings = await _db.UserSettings
            .AsNoTracking()
            .Where(x => x.UserId == userId.Value && (x.Key == InvoiceNumberStartSettingKey || x.Key == InvoiceNumberStrategySettingKey))
            .ToListAsync(ct);

        return Ok(new
        {
            startNumber = ParseStartNumber(settings.FirstOrDefault(x => x.Key == InvoiceNumberStartSettingKey)?.Value),
            allocationStrategy = ParseAllocationStrategy(settings.FirstOrDefault(x => x.Key == InvoiceNumberStrategySettingKey)?.Value),
        });
    }

    [HttpPut("numbering-settings")]
    public async Task<ActionResult<object>> UpsertNumberingSettings([FromBody] InvoiceNumberingSettingsRequest request, CancellationToken ct)
    {
        var userId = GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        if (request.StartNumber < 1)
        {
            return BadRequest("Стартовый номер должен быть больше 0.");
        }

        var allocationStrategy = ParseAllocationStrategy(request.AllocationStrategy);
        var now = DateTime.UtcNow;

        await UpsertUserSettingAsync(userId.Value, InvoiceNumberStartSettingKey, request.StartNumber.ToString(), now, ct);
        await UpsertUserSettingAsync(userId.Value, InvoiceNumberStrategySettingKey, allocationStrategy, now, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            startNumber = request.StartNumber,
            allocationStrategy,
        });
    }

    [HttpGet("next-number")]
    public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct)
    {
        var userId = GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var targetDate = date?.Date ?? await ResolveInvoiceTodayAsync(ct);
        var settings = await _db.UserSettings
            .AsNoTracking()
            .Where(x => x.UserId == userId.Value && (x.Key == InvoiceNumberStartSettingKey || x.Key == InvoiceNumberStrategySettingKey))
            .ToListAsync(ct);

        var startNumber = ParseStartNumber(settings.FirstOrDefault(x => x.Key == InvoiceNumberStartSettingKey)?.Value);
        var allocationStrategy = ParseAllocationStrategy(settings.FirstOrDefault(x => x.Key == InvoiceNumberStrategySettingKey)?.Value);
        var nextNumber = await GetNextInvoiceNumberAsync(targetDate, startNumber, allocationStrategy, ct);

        return Ok(new
        {
            year = targetDate.Year,
            nextNumber = nextNumber.ToString(),
            startNumber,
            allocationStrategy,
        });
    }

    [HttpGet("rules-settings")]
    public async Task<ActionResult<InvoiceRulesSettingsDto>> GetRulesSettings(CancellationToken ct)
    {
        return Ok(await GetInvoiceRulesSettingsAsync(ct));
    }

    [HttpPut("rules-settings")]
    public async Task<ActionResult<InvoiceRulesSettingsDto>> UpsertRulesSettings([FromBody] InvoiceRulesSettingsRequest request, CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        if (request.MaxClientCasesPerInvoice < 1 || request.MaxActsPerInvoice < 1)
        {
            return BadRequest("Лимиты счёта должны быть больше 0.");
        }

        var now = DateTime.UtcNow;
        await UpsertSystemSettingAsync(InvoiceMaxClientCasesSettingKey, request.MaxClientCasesPerInvoice.ToString(), now, ct);
        await UpsertSystemSettingAsync(InvoiceMaxActsSettingKey, request.MaxActsPerInvoice.ToString(), now, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(await GetInvoiceRulesSettingsAsync(ct));
    }

    [HttpGet("document-template-settings")]
    public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> GetDocumentTemplateSettings(CancellationToken ct)
    {
        return Ok(await GetInvoiceDocumentTemplateSettingsAsync(ct));
    }

    [HttpPut("document-template-settings")]
    public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> UpsertDocumentTemplateSettings(
        [FromBody] InvoiceDocumentTemplateSettingsRequest request,
        CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var storage = InvoiceDocumentTemplateEngine.NormalizeRequest(request);
        var validationError = ValidateInvoiceDocumentTemplateSettings(storage);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var now = DateTime.UtcNow;
        await DocumentTemplateHistoryService.SaveVersionedSettingAsync(
            _db,
            InvoiceDocumentTemplatesSettingKey,
            InvoiceDocumentTemplateHistoryObjectId,
            "invoice",
            DocumentTemplateHistoryService.SerializeSettings(InvoiceDocumentTemplateEngine.CreateDefaultSettings()),
            InvoiceDocumentTemplateEngine.Serialize(storage),
            User.GetUserId(),
            User.GetDisplayName(),
            now,
            null,
            ct);
        await _db.SaveChangesAsync(ct);

        return Ok(InvoiceDocumentTemplateEngine.CreateResponse(storage));
    }

    [HttpGet("document-template-settings/history")]
    public async Task<ActionResult<IReadOnlyList<DocumentTemplateVersionDto>>> GetDocumentTemplateHistory(CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        return Ok(await DocumentTemplateHistoryService.GetVersionsAsync(_db, InvoiceDocumentTemplateHistoryObjectId, ct));
    }

    [HttpGet("document-template-settings/history/{versionId:int}")]
    public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> GetDocumentTemplateVersion(
        int versionId,
        CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        var settingsJson = await DocumentTemplateHistoryService.GetVersionSettingsAsync(
            _db,
            InvoiceDocumentTemplateHistoryObjectId,
            versionId,
            ct);
        var request = settingsJson is null ? null : DocumentTemplateHistoryService.DeserializeSettings(settingsJson);
        if (request is null)
        {
            return NotFound();
        }

        return Ok(InvoiceDocumentTemplateEngine.CreateResponse(InvoiceDocumentTemplateEngine.NormalizeRequest(request)));
    }

    [HttpPost("document-template-settings/history/{versionId:int}/restore")]
    public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> RestoreDocumentTemplateVersion(
        int versionId,
        CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        var settingsJson = await DocumentTemplateHistoryService.GetVersionSettingsAsync(
            _db,
            InvoiceDocumentTemplateHistoryObjectId,
            versionId,
            ct);
        if (settingsJson is null)
        {
            return NotFound();
        }

        var request = DocumentTemplateHistoryService.DeserializeSettings(settingsJson);
        if (request is null)
        {
            return BadRequest("Сохраненная версия шаблонов повреждена.");
        }

        var storage = InvoiceDocumentTemplateEngine.NormalizeRequest(request);
        var validationError = ValidateInvoiceDocumentTemplateSettings(storage);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        await DocumentTemplateHistoryService.SaveVersionedSettingAsync(
            _db,
            InvoiceDocumentTemplatesSettingKey,
            InvoiceDocumentTemplateHistoryObjectId,
            "invoice",
            DocumentTemplateHistoryService.SerializeSettings(InvoiceDocumentTemplateEngine.CreateDefaultSettings()),
            InvoiceDocumentTemplateEngine.Serialize(storage),
            User.GetUserId(),
            User.GetDisplayName(),
            DateTime.UtcNow,
            versionId,
            ct);
        await _db.SaveChangesAsync(ct);

        return Ok(InvoiceDocumentTemplateEngine.CreateResponse(storage));
    }


    [HttpPost("migrate-legacy-links")]
    public async Task<ActionResult<InvoiceLegacyMigrationSummaryDto>> MigrateLegacyInvoiceLinks(CancellationToken ct)
    {
        var currentUserId = GetUserId(User);
        var payments = await _db.Payments
            .Include(p => p.Invoice)
            .Include(p => p.InvoiceClientCases)
            .Where(p => p.Account != null && p.Account != "")
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        var numberingSettings = await GetInvoiceNumberingSettingsAsync(currentUserId, ct);
        var reservedInvoiceNumbers = (await _db.Invoices
                .AsNoTracking()
                .Select(x => new { x.Number, x.Date })
                .ToListAsync(ct))
            .Select(x => BuildReservedInvoiceNumberKey(x.Number, x.Date))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var summary = new InvoiceLegacyMigrationSummaryDto
        {
            ScannedPayments = payments.Count,
            InvoicePayments = payments.Count(p => p.DocumentKind == DocumentKind.Invoice),
        };

        var bindings = new List<InvoiceMigrationBinding>();
        foreach (var payment in payments)
        {
            var binding = TryBuildInvoiceMigrationBinding(payment, preferCanonicalInvoice: false);
            if (binding is null)
            {
                if (payment.ClientId is null || payment.ClientId.Value <= 0)
                {
                    summary.MissingClientSkipped++;
                }

                if (string.IsNullOrWhiteSpace(Normalize(payment.Account)))
                {
                    summary.MissingNumberSkipped++;
                }

                continue;
            }

            bindings.Add(binding);
        }

        summary.DistinctLegacyInvoiceKeys = bindings
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        summary.DistinctLegacyGroups = summary.DistinctLegacyInvoiceKeys;
        summary.GroupsWithCase = bindings
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.First().ClientCaseId.HasValue);
        summary.GroupsWithoutCase = summary.DistinctLegacyGroups - summary.GroupsWithCase;

        var invoicesById = await _db.Invoices.ToDictionaryAsync(x => x.Id, ct);
        var candidateInvoiceIdsByKey = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var keyCountsByInvoiceId = new Dictionary<int, Dictionary<string, int>>();

        foreach (var payment in payments.Where(x => x.InvoiceId.HasValue && x.Invoice != null))
        {
            var binding = TryBuildInvoiceMigrationBinding(payment, preferCanonicalInvoice: true);
            if (binding is null || !payment.InvoiceId.HasValue)
            {
                continue;
            }

            IncrementNestedCount(candidateInvoiceIdsByKey, binding.Key, payment.InvoiceId.Value);
            IncrementNestedCount(keyCountsByInvoiceId, payment.InvoiceId.Value, binding.Key);
        }

        summary.ExistingDuplicateInvoiceKeys = candidateInvoiceIdsByKey.Count(x => x.Value.Count > 1);
        summary.SharedInvoiceEntities = keyCountsByInvoiceId.Count(x => x.Value.Count > 1);

        var preservedKeyByInvoiceId = keyCountsByInvoiceId.ToDictionary(
            x => x.Key,
            x => x.Value
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Key);

        var invoiceByKey = new Dictionary<string, Invoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in candidateInvoiceIdsByKey)
        {
            var reusableInvoiceId = entry.Value
                .Where(candidate => invoicesById.ContainsKey(candidate.Key))
                .Where(candidate =>
                {
                    if (!keyCountsByInvoiceId.TryGetValue(candidate.Key, out var mappedKeys) || mappedKeys.Count <= 1)
                    {
                        return true;
                    }

                    return preservedKeyByInvoiceId.TryGetValue(candidate.Key, out var preservedKey)
                        && string.Equals(preservedKey, entry.Key, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(candidate => candidate.Value)
                .ThenBy(candidate => candidate.Key)
                .Select(candidate => candidate.Key)
                .FirstOrDefault();

            if (reusableInvoiceId > 0 && invoicesById.TryGetValue(reusableInvoiceId, out var invoice))
            {
                invoiceByKey[entry.Key] = invoice;
            }
        }

        var groupedBindings = bindings
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groupedBindings)
        {
            var orderedBindings = group
                .OrderBy(x => x.InvoiceDate)
                .ThenBy(x => x.Payment.Id)
                .ToList();
            var first = orderedBindings[0];
            var hadLegacyCandidates = candidateInvoiceIdsByKey.ContainsKey(group.Key);
            var reusedExistingInvoice = invoiceByKey.TryGetValue(group.Key, out var targetInvoice);

            if (!reusedExistingInvoice)
            {
                var resolvedNumber = await ResolveMigrationInvoiceNumberAsync(
                    first.Number,
                    first.InvoiceDate,
                    numberingSettings.StartNumber,
                    numberingSettings.AllocationStrategy,
                    reservedInvoiceNumbers,
                    ct);

                targetInvoice = new Invoice
                {
                    Number = resolvedNumber,
                    Date = first.InvoiceDate,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Invoices.Add(targetInvoice);
                reservedInvoiceNumbers.Add(BuildReservedInvoiceNumberKey(targetInvoice.Number, targetInvoice.Date));
                invoiceByKey[group.Key] = targetInvoice;
                summary.CreatedInvoices++;

                if (hadLegacyCandidates)
                {
                    summary.SplitSharedInvoiceGroups++;
                }
            }
            else
            {
                summary.LinkedToExistingInvoice++;
            }

            var resolvedInvoice = targetInvoice!;
            var normalizedAccountDate = resolvedInvoice.Date.Date == default
                ? first.InvoiceDate.Date
                : resolvedInvoice.Date.Date;
            var normalizedAccountNumber = Normalize(resolvedInvoice.Number) ?? first.Number;

            foreach (var binding in orderedBindings)
            {
                var payment = binding.Payment;
                AssignLegacyAccountSource(payment, binding.Number, binding.InvoiceDate);
                var alreadyLinkedToTarget = reusedExistingInvoice
                    && resolvedInvoice.Id > 0
                    && payment.InvoiceId == resolvedInvoice.Id;

                if (alreadyLinkedToTarget)
                {
                    summary.AlreadyLinked++;
                }
                else if (payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
                {
                    summary.RelinkedPayments++;
                }
                else
                {
                    summary.LinkedPayments++;
                }

                payment.Invoice = resolvedInvoice;
                payment.InvoiceId = resolvedInvoice.Id > 0 ? resolvedInvoice.Id : null;
                payment.Account = normalizedAccountNumber;
                payment.AccountDate = normalizedAccountDate;
                payment.ClientId = binding.ClientId;
                payment.ClientCaseId = binding.ClientCaseId;
            }

            var caseCarrierPayments = orderedBindings
                .Select(x => x.Payment)
                .Where(x => x.DocumentKind == DocumentKind.Invoice)
                .DistinctBy(x => x.Id)
                .ToList();

            if (caseCarrierPayments.Count == 0)
            {
                caseCarrierPayments.Add(orderedBindings[0].Payment);
            }

            foreach (var carrier in caseCarrierPayments)
            {
                if (SyncInvoiceMigrationCaseLinks(carrier, first.ClientId, first.ClientCaseId))
                {
                    summary.RepresentativePaymentsUpdated++;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(summary);
    }

    [HttpPost("cleanup-counterparty-names")]
    public async Task<ActionResult<object>> CleanupCounterpartyNames(CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        var invoicePayments = await _db.Payments
            .Where(p => p.DocumentKind == DocumentKind.Invoice && p.CounterpartyName != null && p.CounterpartyName != "")
            .ToListAsync(ct);

        foreach (var payment in invoicePayments)
        {
            payment.CounterpartyName = null;
        }

        var retiredRequirements = await _db.FormFieldRequirements
            .Where(x => x.Section == "contract-invoices" && x.FieldKey == "CounterpartyName")
            .ToListAsync(ct);

        if (retiredRequirements.Count > 0)
        {
            _db.FormFieldRequirements.RemoveRange(retiredRequirements);
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            cleared = invoicePayments.Count,
            removedRequirements = retiredRequirements.Count,
        });
    }

    [HttpPost("cleanup-orphan-invoices")]
    public async Task<ActionResult<object>> CleanupOrphanInvoices(CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        var orphanInvoices = await _db.Invoices
            .Where(invoice => !invoice.Payments.Any())
            .OrderByDescending(invoice => invoice.Date)
            .ThenByDescending(invoice => invoice.Id)
            .ToListAsync(ct);

        var deletedItems = orphanInvoices
            .Select(invoice => new
            {
                invoice.Id,
                invoice.Number,
                invoice.Date,
            })
            .ToList();

        if (orphanInvoices.Count == 0)
        {
            return Ok(new
            {
                deleted = 0,
                items = deletedItems,
            });
        }

        _db.Invoices.RemoveRange(orphanInvoices);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            deleted = orphanInvoices.Count,
            items = deletedItems,
        });
    }

    [HttpPost("{invoiceEntityId:int}/repair-legacy-installments")]
    public async Task<ActionResult<InvoiceLegacyInstallmentRepairSummaryDto>> RepairLegacyInstallments(
        int invoiceEntityId,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (!await IsCurrentUserAdminAsync(ct))
        {
            return Forbid();
        }

        var payments = await _db.Payments
            .Include(payment => payment.Invoice)
            .Include(payment => payment.InvoiceClientCases)
            .Include(payment => payment.Responsibles)
            .Where(payment => payment.InvoiceId == invoiceEntityId)
            .OrderBy(payment => payment.Id)
            .ToListAsync(ct);

        if (payments.Count == 0)
        {
            return NotFound();
        }

        var representative = SelectRepresentativeInvoicePayment(payments);
        var representativeInvoice = representative?.Invoice
            ?? await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(invoice => invoice.Id == invoiceEntityId, ct);
        var childPayments = representative is null
            ? new List<Payment>()
            : payments
                .Where(payment => payment.Id != representative.Id)
                .OrderBy(payment => payment.PlannedDate == default ? payment.Date : payment.PlannedDate)
                .ThenBy(payment => payment.Date)
                .ThenBy(payment => payment.Id)
                .ToList();
        var invoiceNumber = Normalize(representativeInvoice?.Number) ?? Normalize(representative?.Account) ?? string.Empty;
        var invoiceAmount = representative?.Amount ?? 0m;
        var childPaymentsTotal = childPayments.Sum(payment => payment.Amount);
        var legacyAggregateDetected = representative is not null && IsLegacyBrokenInstallmentAggregate(payments);
        var convertedLegacyAggregateDetected = representative is not null && IsConvertedLegacyInstallmentAggregate(payments, representative);
        var summary = new InvoiceLegacyInstallmentRepairSummaryDto
        {
            InvoiceEntityId = invoiceEntityId,
            InvoiceNumber = invoiceNumber,
            RepresentativePaymentId = representative?.Id ?? 0,
            ScannedPayments = payments.Count,
            ChildPayments = childPayments.Count,
            InvoiceAmount = invoiceAmount,
            ChildPaymentsTotal = childPaymentsTotal,
            RemainingUnscheduledAmount = Math.Max(invoiceAmount - childPaymentsTotal, 0m),
            DryRun = dryRun,
            LegacyAggregateDetected = legacyAggregateDetected,
            ConvertedLegacyAggregateDetected = convertedLegacyAggregateDetected,
        };

        if (representative is not null && convertedLegacyAggregateDetected)
        {
            var repairLocalToday = await ResolveInvoiceTodayAsync(ct);
            RepairConvertedLegacyInstallmentAggregate(
                payments,
                representative,
                invoiceEntityId,
                representativeInvoice,
                summary,
                dryRun,
                repairLocalToday);
            if (dryRun)
            {
                _db.ChangeTracker.Clear();
                return Ok(summary);
            }

            await _db.SaveChangesAsync(ct);
            return Ok(summary);
        }

        if (representative is null || !legacyAggregateDetected)
        {
            return Ok(summary);
        }

        var invoiceDate = representativeInvoice?.Date.Date
            ?? representative.AccountDate?.Date
            ?? representative.Date.Date;
        var localToday = await ResolveInvoiceTodayAsync(ct);
        var installmentPlan = BuildLegacyRepairInstallmentPlan(invoiceAmount, childPayments, localToday);
        var effectiveClientId = representative.ClientId
            ?? childPayments.FirstOrDefault(payment => payment.ClientId.HasValue && payment.ClientId.Value > 0)?.ClientId;
        var effectiveClientCaseId = representative.ClientCaseId is > 0
            ? representative.ClientCaseId
            : childPayments.FirstOrDefault(payment => payment.ClientCaseId.HasValue && payment.ClientCaseId.Value > 0)?.ClientCaseId;

        for (var index = 0; index < childPayments.Count; index++)
        {
            var payment = childPayments[index];
            var normalized = false;

            if (payment.InvoiceId != invoiceEntityId)
            {
                payment.InvoiceId = invoiceEntityId;
                normalized = true;
            }

            if (!string.Equals(Normalize(payment.Account), invoiceNumber, StringComparison.OrdinalIgnoreCase))
            {
                payment.Account = invoiceNumber;
                normalized = true;
            }

            if (payment.AccountDate?.Date != invoiceDate)
            {
                payment.AccountDate = invoiceDate;
                normalized = true;
            }

            if (payment.DocumentKind != DocumentKind.Payment)
            {
                payment.DocumentKind = DocumentKind.Payment;
                summary.UpdatedKinds++;
            }

            var item = installmentPlan.Items[index];
            var mergedSystemNotes = MergeInstallmentMetadataIntoSystemNotes(payment.SystemNotes, installmentPlan, item);
            if (!string.Equals(payment.SystemNotes, mergedSystemNotes, StringComparison.Ordinal))
            {
                payment.SystemNotes = mergedSystemNotes;
                summary.AddedMarkers++;
            }

            PaymentDomainService.Normalize(payment);

            if (normalized)
            {
                summary.NormalizedLinks++;
            }
        }

        if (effectiveClientId.HasValue
            && effectiveClientId.Value > 0
            && SyncInvoiceMigrationCaseLinks(representative, effectiveClientId.Value, effectiveClientCaseId))
        {
            summary.UpdatedCaseLinks++;
        }

        summary.Repaired = summary.UpdatedKinds > 0
            || summary.AddedMarkers > 0
            || summary.NormalizedLinks > 0
            || summary.UpdatedCaseLinks > 0;

        if (dryRun)
        {
            _db.ChangeTracker.Clear();
            return Ok(summary);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(summary);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var payment = await BuildEditableInvoiceDeletionTargetsQuery(visibility)
            .Include(p => p.Invoice)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var invoiceIdsToDelete = GetInvoiceEntityIdsToDelete(new[] { payment });
        var paymentsToDelete = await ExpandInvoiceAggregatePaymentsAsync(new[] { payment }, ct);
        var deletionError = await ValidateInvoiceCanBeDeletedAsync(paymentsToDelete, ct);
        if (deletionError is not null)
        {
            return deletionError;
        }

        await DetachActsFromDeletedInvoicePaymentsAsync(paymentsToDelete, ct);

        _db.Payments.RemoveRange(paymentsToDelete);

        if (invoiceIdsToDelete.Count > 0)
        {
            var invoices = await _db.Invoices
                .Where(invoice => invoiceIdsToDelete.Contains(invoice.Id))
                .ToListAsync(ct);

            _db.Invoices.RemoveRange(invoices);
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkIdsRequest request, CancellationToken ct)
    {
        var ids = NormalizeIds(request.Ids.Select(id => (int?)id));
        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Не выбраны счета для удаления." });
        }

        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        if (await RequireAccountsPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var paymentsQuery = BuildEditableInvoiceDeletionTargetsQuery(visibility);

        var payments = await paymentsQuery
            .Include(payment => payment.Invoice)
            .Where(payment => ids.Contains(payment.Id))
            .ToListAsync(ct);

        var foundIds = payments.Select(payment => payment.Id).ToHashSet();
        var missingIds = ids.Where(id => !foundIds.Contains(id)).ToList();
        if (missingIds.Count > 0)
        {
            return NotFound(new
            {
                message = "Не удалось найти часть счетов для удаления.",
                missingIds,
            });
        }

        var invoiceIdsToDelete = GetInvoiceEntityIdsToDelete(payments);
        var paymentsToDelete = await ExpandInvoiceAggregatePaymentsAsync(payments, ct);
        var deletionError = await ValidateInvoiceCanBeDeletedAsync(paymentsToDelete, ct);
        if (deletionError is not null)
        {
            return deletionError;
        }

        await DetachActsFromDeletedInvoicePaymentsAsync(paymentsToDelete, ct);

        _db.Payments.RemoveRange(paymentsToDelete);

        if (invoiceIdsToDelete.Count > 0)
        {
            var invoices = await _db.Invoices
                .Where(invoice => invoiceIdsToDelete.Contains(invoice.Id))
                .ToListAsync(ct);

            _db.Invoices.RemoveRange(invoices);
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new { deleted = paymentsToDelete.Count });
    }

    private static List<int> GetInvoiceEntityIdsToDelete(IEnumerable<Payment> payments)
    {
        return payments
            .Where(payment => payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
            .Select(payment => payment.InvoiceId!.Value)
            .Distinct()
            .ToList();
    }

    private async Task<List<Payment>> ExpandInvoiceAggregatePaymentsAsync(IEnumerable<Payment> payments, CancellationToken ct)
    {
        var paymentsById = payments.ToDictionary(payment => payment.Id);
        var invoiceIdsToDelete = GetInvoiceEntityIdsToDelete(paymentsById.Values);

        if (invoiceIdsToDelete.Count == 0)
        {
            return paymentsById.Values.ToList();
        }

        var linkedPayments = await _db.Payments
            .Where(payment => payment.InvoiceId.HasValue && invoiceIdsToDelete.Contains(payment.InvoiceId.Value))
            .ToListAsync(ct);

        foreach (var linkedPayment in linkedPayments)
        {
            paymentsById[linkedPayment.Id] = linkedPayment;
        }

        return paymentsById.Values.ToList();
    }

    private async Task<IActionResult?> ValidateInvoiceCanBeDeletedAsync(IEnumerable<Payment> payments, CancellationToken ct)
    {
        var paymentList = payments.ToList();
        var paymentIds = paymentList
            .Select(payment => payment.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (paymentIds.Count == 0)
        {
            return null;
        }

        var invoiceEntityIds = GetInvoiceEntityIdsToDelete(paymentList);
        var linkedContractCount = await _db.ContractInvoices
            .CountAsync(link => paymentIds.Contains(link.InvoiceId), ct);
        linkedContractCount += paymentList.Count(payment => payment.ContractId.HasValue && payment.ContractId.Value > 0);
        var linkedMoneyOperationCount = invoiceEntityIds.Count == 0
            ? 0
            : await _db.MoneyOperations
                .CountAsync(operation => operation.LinkedInvoiceId.HasValue && invoiceEntityIds.Contains(operation.LinkedInvoiceId.Value), ct);

        if (linkedContractCount == 0 && linkedMoneyOperationCount == 0)
        {
            return null;
        }

        var linkedParts = new List<string>();
        if (linkedContractCount > 0)
        {
            linkedParts.Add($"договоры: {linkedContractCount}");
        }

        if (linkedMoneyOperationCount > 0)
        {
            linkedParts.Add($"движения денег: {linkedMoneyOperationCount}");
        }

        return BadRequest(new
        {
            message = $"Нельзя удалить счёт, пока к нему привязаны документы ({string.Join(", ", linkedParts)}). Сначала отвяжите связанные документы или аннулируйте счёт.",
        });
    }

    private async Task DetachActsFromDeletedInvoicePaymentsAsync(IEnumerable<Payment> payments, CancellationToken ct)
    {
        var representativePaymentIds = payments
            .Where(IsInvoiceDocumentPayment)
            .Select(payment => payment.Id)
            .Distinct()
            .ToList();

        if (representativePaymentIds.Count == 0)
        {
            return;
        }

        var linkedActs = await _db.Acts
            .Where(act => act.InvoiceId.HasValue && representativePaymentIds.Contains(act.InvoiceId.Value))
            .ToListAsync(ct);

        if (linkedActs.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var act in linkedActs)
        {
            act.InvoiceId = null;
            act.InvoiceNumber = null;
            act.UpdatedAt = now;
        }
    }

    private static bool IsInvoiceDocumentPayment(Payment payment)
    {
        return payment.DocumentKind == DocumentKind.Invoice && payment.InvoiceId.HasValue;
    }

    [HttpPost("change-responsible")]
    public async Task<IActionResult> ChangeResponsible([FromBody] BulkAssignResponsibleRequest request, CancellationToken ct)
    {
        var ids = NormalizeIds(request.Ids.Select(id => (int?)id));
        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Не выбраны счета для смены ответственного." });
        }

        var responsibleIds = new HashSet<int> { request.ResponsibleUserId };
        if (!await EnsureResponsibleIdsExistAsync(responsibleIds, ct))
        {
            return BadRequest(new { message = "Указанный ответственный не найден." });
        }

        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return MissingAccountsVisibilityResult();
        }
        var paymentsQuery = BuildEditableInvoicePaymentsQuery(visibility);

        var payments = await paymentsQuery
            .Include(payment => payment.Invoice)
            .Include(payment => payment.Responsibles)
            .Where(payment => ids.Contains(payment.Id))
            .ToListAsync(ct);

        var foundIds = payments.Select(payment => payment.Id).ToHashSet();
        var missingIds = ids.Where(id => !foundIds.Contains(id)).ToList();
        if (missingIds.Count > 0)
        {
            return NotFound(new
            {
                message = "Не удалось найти часть счетов для смены ответственного.",
                missingIds,
            });
        }

        var now = DateTime.UtcNow;
        var aggregatePaymentIds = (await ExpandInvoiceAggregatePaymentsAsync(payments, ct))
            .Select(payment => payment.Id)
            .Distinct()
            .ToList();

        var aggregatePayments = await _db.Payments
            .Include(payment => payment.Invoice)
            .Include(payment => payment.Responsibles)
            .Where(payment => aggregatePaymentIds.Contains(payment.Id))
            .ToListAsync(ct);

        foreach (var payment in aggregatePayments)
        {
            var forRemoval = payment.Responsibles.ToList();
            foreach (var row in forRemoval)
            {
                payment.Responsibles.Remove(row);
            }

            payment.Responsibles.Add(new PaymentResponsible
            {
                PaymentId = payment.Id,
                UserId = request.ResponsibleUserId,
            });

            if (payment.Invoice is not null)
            {
                payment.Invoice.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            updated = payments.Count,
            responsibleUserId = request.ResponsibleUserId,
        });
    }


    [HttpGet("{id:int}/document")]
    public async Task<IActionResult> GetDocument(
        int id,
        [FromQuery] string? format = "word",
        [FromQuery] string? templateKey = null,
        [FromQuery] bool? includeSignatureAndSeal = null,
        CancellationToken ct = default)
    {
        if (await RequireAccountsPermissionAsync(permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var payment = await LoadInvoiceDocumentPaymentAsync(id, ct);

        if (payment is null)
        {
            return NotFound();
        }

        var normalizedFormat = NormalizeDocumentFormat(format);
        var lineItems = await LoadInvoiceLineItemsAsync(payment.Id, payment.SystemNotes, ct);
        var installmentDocumentContext = await BuildInstallmentDocumentContextAsync(payment, lineItems, ct);
        var documentSettings = await GetInvoiceDocumentTemplateSettingsAsync(ct);
        var template = SelectInvoiceDocumentTemplate(documentSettings, templateKey);
        await RememberInvoiceDocumentTemplateAsync(payment, template.Key, ct);
        var mainCompany = await LoadMainCompanyAsync(ct);
        var shouldIncludeSignatureAndSeal = includeSignatureAndSeal ?? documentSettings.PrintWithSignatureAndSealByDefault;
        var documentHtml = InvoiceDocumentTemplateEngine.RenderHtml(
            template.Html,
            payment,
            installmentDocumentContext.LineItems,
            mainCompany,
            shouldIncludeSignatureAndSeal,
            installmentDocumentContext.ScheduleHtml);

        if (normalizedFormat == "html")
        {
            return Content(documentHtml, "text/html; charset=utf-8");
        }

        var isOrderTemplate = string.Equals(template.Key, InvoiceDocumentTemplateEngine.OrderTemplateKey, StringComparison.OrdinalIgnoreCase);
        var isOfferTemplate = string.Equals(template.Key, InvoiceDocumentTemplateEngine.OfferTemplateKey, StringComparison.OrdinalIgnoreCase);
        var bytes = normalizedFormat == "word"
            ? isOrderTemplate
                ? InvoiceOrderWordDocumentRenderer.Render(documentHtml)
                : isOfferTemplate
                    ? InvoiceOfferWordDocumentRenderer.Render(documentHtml)
                    : InvoiceStandardWordDocumentRenderer.Render(documentHtml)
            : Encoding.UTF8.GetBytes(documentHtml);
        var isDocx = normalizedFormat == "word";
        var ext = normalizedFormat == "pdf" ? "pdf" : isDocx ? "docx" : "doc";
        var fileName = $"invoice-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.{ext}";

        if (normalizedFormat == "pdf")
        {
            try
            {
                var pdfBytes = await RenderInvoicePdfAsync(documentHtml, ct);
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }
        }

        return File(bytes, isDocx ? PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType : "application/msword", fileName);
    }

    [HttpPost("{id:int}/yandex-disk")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> UploadDocumentToYandexDisk(
        int id,
        [FromQuery] string? format = "pdf",
        [FromQuery] string? templateKey = null,
        [FromQuery] bool? includeSignatureAndSeal = null,
        [FromQuery] bool? overwrite = null,
        CancellationToken ct = default)
    {
        if (_yandexDiskService is null)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "Сервис Яндекс.Диска не зарегистрирован на сервере.");
        }

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.YandexDisk, permission => permission.CanCreate, ct) is { } yandexForbidden)
        {
            return yandexForbidden;
        }

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Accounts, permission => permission.CanExport, ct) is { } invoiceForbidden)
        {
            return invoiceForbidden;
        }

        var payment = await LoadInvoiceDocumentPaymentAsync(id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var normalizedFormat = NormalizeDocumentFormat(format);
        if (normalizedFormat == "html")
        {
            normalizedFormat = "word";
        }

        var lineItems = await LoadInvoiceLineItemsAsync(payment.Id, payment.SystemNotes, ct);
        var installmentDocumentContext = await BuildInstallmentDocumentContextAsync(payment, lineItems, ct);
        var documentSettings = await GetInvoiceDocumentTemplateSettingsAsync(ct);
        var template = SelectInvoiceDocumentTemplate(documentSettings, templateKey);
        await RememberInvoiceDocumentTemplateAsync(payment, template.Key, ct);
        var mainCompany = await LoadMainCompanyAsync(ct);
        var shouldIncludeSignatureAndSeal = includeSignatureAndSeal ?? documentSettings.PrintWithSignatureAndSealByDefault;
        var documentHtml = InvoiceDocumentTemplateEngine.RenderHtml(
            template.Html,
            payment,
            installmentDocumentContext.LineItems,
            mainCompany,
            shouldIncludeSignatureAndSeal,
            installmentDocumentContext.ScheduleHtml);

        var documentNumber = payment.Invoice?.Number ?? payment.Account;
        byte[] bytes;
        string fileName;
        string contentType;
        if (normalizedFormat == "pdf")
        {
            try
            {
                bytes = await RenderInvoicePdfAsync(documentHtml, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }

            fileName = $"invoice-{SanitizeFilePart(documentNumber)}.pdf";
            contentType = "application/pdf";
        }
        else if (string.Equals(template.Key, InvoiceDocumentTemplateEngine.OrderTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            bytes = InvoiceOrderWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-order-{SanitizeFilePart(documentNumber)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        else if (string.Equals(template.Key, InvoiceDocumentTemplateEngine.DefaultTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            bytes = InvoiceStandardWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-{SanitizeFilePart(documentNumber)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        else if (string.Equals(template.Key, InvoiceDocumentTemplateEngine.OfferTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            bytes = InvoiceOfferWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-offer-{SanitizeFilePart(documentNumber)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        else
        {
            bytes = InvoiceStandardWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-{SanitizeFilePart(documentNumber)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }

        await using var stream = new MemoryStream(bytes);
        try
        {
            var result = await _yandexDiskService.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
                stream,
                contentType,
                fileName,
                "invoice",
                "Счет",
                documentNumber,
                payment.Invoice?.Date ?? payment.AccountDate ?? payment.Date,
                string.IsNullOrWhiteSpace(payment.Description) ? $"Счет {documentNumber}" : payment.Description,
                ResolveInvoiceClientName(payment),
                ResolveInvoiceLegalEntityName(payment),
                payment.ClientCase?.CaseNumber ?? payment.InvoiceClientCases.FirstOrDefault()?.ClientCase?.CaseNumber,
                payment.ClientCase?.Title ?? payment.InvoiceClientCases.FirstOrDefault()?.ClientCase?.Title,
                overwrite),
                ct);

            await YandexDiskCrmSyncStateRecorder.RecordSuccessAsync(
                _db,
                "invoice",
                "payment",
                payment.Id,
                fileName,
                result.Path,
                null,
                ct);

            _db.ObjectAuditEvents.Add(new ObjectAuditEvent
            {
                ObjectType = "payment",
                ObjectId = payment.Id,
                Action = "YandexDiskUploaded",
                Description = $"Счет «{documentNumber}» отправлен на Яндекс.Диск.",
                ActorUserId = User.GetUserId(),
                ActorUserName = User.GetDisplayName(),
                MetadataJson = JsonSerializer.Serialize(new { payment.Id, DocumentNumber = documentNumber, FileName = fileName, result.Path }),
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch (YandexDiskOperationException ex)
        {
            await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
                _db,
                "invoice",
                "payment",
                payment.Id,
                fileName,
                null,
                ex,
                ct);
            await _db.SaveChangesAsync(ct);
            return MapYandexDiskError(ex);
        }
    }

    [HttpPost("{id:int}/send-email")]
    public async Task<IActionResult> SendInvoiceEmail(int id, [FromBody] SendInvoiceEmailRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (await RequireAccountsPermissionAsync(permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var payment = await LoadInvoiceDocumentPaymentAsync(id, ct);

        if (payment is null)
        {
            return NotFound();
        }

        var email = string.IsNullOrWhiteSpace(request.Email)
            ? payment.Client?.Email?.Trim()
            : request.Email.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Recipient email is required.");
        }

        var normalizedFormat = NormalizeDocumentFormat(request.Format);
        if (normalizedFormat == "html")
        {
            normalizedFormat = "word";
        }

        var lineItems = await LoadInvoiceLineItemsAsync(payment.Id, payment.SystemNotes, ct);
        var installmentDocumentContext = await BuildInstallmentDocumentContextAsync(payment, lineItems, ct);
        var documentSettings = await GetInvoiceDocumentTemplateSettingsAsync(ct);
        var template = SelectInvoiceDocumentTemplate(documentSettings, null);
        var mainCompany = await LoadMainCompanyAsync(ct);
        var documentHtml = InvoiceDocumentTemplateEngine.RenderHtml(
            template.Html,
            payment,
            installmentDocumentContext.LineItems,
            mainCompany,
            documentSettings.PrintWithSignatureAndSealByDefault,
            installmentDocumentContext.ScheduleHtml);
        byte[] bytes;
        string fileName;
        string contentType;
        if (normalizedFormat == "pdf")
        {
            try
            {
                bytes = await RenderInvoicePdfAsync(documentHtml, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }

            fileName = $"invoice-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.pdf";
            contentType = "application/pdf";
        }
        else if (string.Equals(template.Key, InvoiceDocumentTemplateEngine.OrderTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            bytes = InvoiceOrderWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-order-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        else if (string.Equals(template.Key, InvoiceDocumentTemplateEngine.DefaultTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            bytes = InvoiceStandardWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        else if (string.Equals(template.Key, InvoiceDocumentTemplateEngine.OfferTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            bytes = InvoiceOfferWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-offer-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        else
        {
            bytes = InvoiceStandardWordDocumentRenderer.Render(documentHtml);
            fileName = $"invoice-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.docx";
            contentType = PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType;
        }
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? $"Счёт №{payment.Invoice?.Number ?? payment.Account} от {(payment.Invoice?.Date ?? payment.AccountDate)?.Date:dd.MM.yyyy}"
            : request.Subject.Trim();
        var body = string.IsNullOrWhiteSpace(request.Message)
            ? "Во вложении счёт."
            : request.Message.Trim();

        await _emailService.SendEmailWithAttachmentAsync(
            email,
            subject,
            body,
            fileName,
            bytes,
            contentType);

        return Ok(new { sent = true, recipient = email, fileName, format = normalizedFormat });
    }

    private Task<byte[]> RenderInvoicePdfAsync(string documentHtml, CancellationToken ct)
    {
        if (_invoiceDocumentPdfService is null)
        {
            throw new InvalidOperationException(
                "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения.");
        }

        return _invoiceDocumentPdfService.RenderAsync(documentHtml, ct);
    }

    private async Task<ActionResult?> RequireSectionPermissionAsync(
        string section,
        Func<RolePermission, bool> isAllowed,
        CancellationToken ct)
    {
        if (await IsCurrentUserAdminAsync(ct))
        {
            return null;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, section, ct);
        return permission is not null && permission.CanView && isAllowed(permission)
            ? null
            : Forbid();
    }

    private static ActionResult MapYandexDiskError(YandexDiskOperationException ex)
    {
        if (ex.StatusCode >= 400 && ex.StatusCode < 500)
        {
            return new BadRequestObjectResult(ex.Message);
        }

        return new ObjectResult(ex.Message)
        {
            StatusCode = StatusCodes.Status502BadGateway,
        };
    }

    private static string? ResolveInvoiceClientName(Payment payment)
    {
        var client = payment.Type == PaymentType.Expense
            ? payment.ExpenseClient
            : payment.Client;

        return client?.Name ?? payment.CounterpartyName;
    }

    private static string? ResolveInvoiceLegalEntityName(Payment payment)
    {
        var legalEntity = payment.Type == PaymentType.Expense
            ? payment.ExpenseLegalEntity
            : payment.LegalEntity;

        return legalEntity?.ShortName ?? legalEntity?.FullName;
    }

    private IQueryable<InvoiceQueryItem> BuildInvoiceQuery(DataVisibilityAccess? visibility = null)
    {
        var payments = BuildVisibleInvoicePaymentsQuery(trackChanges: false, visibility);

        return payments.Select(payment => new InvoiceQueryItem
        {
            Payment = payment,
            Act = _db.Acts.AsNoTracking()
                .Where(a => a.InvoiceId == payment.Id
                    || (a.InvoiceId == null && a.InvoiceNumber != null && a.InvoiceNumber == (payment.Invoice != null ? payment.Invoice.Number : payment.Account)))
                .OrderByDescending(a => a.Date)
                .ThenByDescending(a => a.Id)
                .FirstOrDefault(),
        });
    }

    private IQueryable<Payment> BuildEditableInvoicePaymentsQuery(DataVisibilityAccess? visibility = null)
    {
        return BuildVisibleInvoicePaymentsQuery(trackChanges: true, visibility);
    }

    private IQueryable<Payment> BuildEditableInvoiceDeletionTargetsQuery(DataVisibilityAccess? visibility = null)
    {
        var visiblePayments = visibility is null
            ? _db.Payments
            : _db.Payments.ApplyPaymentVisibility(visibility);

        return visiblePayments
            .Where(payment =>
                (payment.InvoiceId.HasValue
                    && payment.InvoiceId.Value > 0
                    && payment.Invoice != null)
                || (!payment.InvoiceId.HasValue
                    && payment.DocumentKind == DocumentKind.Invoice
                    && payment.Account != null
                    && payment.Account != ""));
    }

    private IQueryable<Payment> BuildVisibleInvoicePaymentsQuery(bool trackChanges, DataVisibilityAccess? visibility = null)
    {
        var payments = trackChanges
            ? _db.Payments
            : _db.Payments.AsNoTracking();

        var visiblePayments = visibility is null
            ? payments
            : payments.ApplyPaymentVisibility(visibility);

        return visiblePayments
            .Where(payment =>
            (payment.InvoiceId.HasValue
                && payment.Invoice != null
                && payment.Id == _db.Payments
                    .Where(candidate => candidate.InvoiceId == payment.InvoiceId)
                    .OrderByDescending(candidate => candidate.DocumentKind == DocumentKind.Invoice)
                    .ThenBy(candidate => candidate.Id)
                    .Select(candidate => candidate.Id)
                    .FirstOrDefault())
            || (!payment.InvoiceId.HasValue
                && payment.DocumentKind == DocumentKind.Invoice
                && payment.Account != null
                && payment.Account != ""));
    }

    private IQueryable<InvoiceQueryItem> ApplyFilters(
        IQueryable<InvoiceQueryItem> query,
        DateTime? from,
        DateTime? to,
        PaymentStatus? status,
        int? clientId,
        int? legalEntityId,
        int? contractId,
        int? caseId,
        int? responsibleId,
        string? search,
        PaymentType? type,
        string? clientRole,
        IReadOnlySet<int>? clientIds = null)
    {
        if (type.HasValue)
        {
            query = query.Where(x => x.Payment.Type == type.Value);
        }

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(x => (x.Payment.Invoice != null ? x.Payment.Invoice.Date : (x.Payment.AccountDate ?? x.Payment.Date)) >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(x => (x.Payment.Invoice != null ? x.Payment.Invoice.Date : (x.Payment.AccountDate ?? x.Payment.Date)) <= toDate);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Payment.Status == status.Value);
        }

        if (clientId.HasValue)
        {
            var normalizedClientRole = NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(x => x.Payment.ClientId == clientId.Value),
                "expenseClient" => query.Where(x => x.Payment.ExpenseClientId == clientId.Value),
                _ => query.Where(x => x.Payment.ClientId == clientId.Value || x.Payment.ExpenseClientId == clientId.Value),
            };
        }
        else if (clientIds is { Count: > 0 })
        {
            var normalizedClientRole = NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(x => x.Payment.ClientId.HasValue && clientIds.Contains(x.Payment.ClientId.Value)),
                "expenseClient" => query.Where(x => x.Payment.ExpenseClientId.HasValue && clientIds.Contains(x.Payment.ExpenseClientId.Value)),
                _ => query.Where(x =>
                    (x.Payment.ClientId.HasValue && clientIds.Contains(x.Payment.ClientId.Value))
                    || (x.Payment.ExpenseClientId.HasValue && clientIds.Contains(x.Payment.ExpenseClientId.Value))),
            };
        }

        if (legalEntityId.HasValue)
        {
            var normalizedClientRole = NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(x => x.Payment.LegalEntityId == legalEntityId.Value),
                "expenseClient" => query.Where(x => x.Payment.ExpenseLegalEntityId == legalEntityId.Value),
                _ => query.Where(x =>
                    x.Payment.LegalEntityId == legalEntityId.Value
                    || x.Payment.ExpenseLegalEntityId == legalEntityId.Value),
            };
        }

        if (contractId.HasValue)
        {
            query = query.Where(x =>
                x.Payment.ContractId == contractId.Value
                || _db.ContractInvoices.Any(link => link.ContractId == contractId.Value && link.InvoiceId == x.Payment.Id));
        }

        if (caseId.HasValue)
        {
            query = query.Where(x =>
                x.Payment.ClientCaseId == caseId.Value
                || x.Payment.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value));
        }

        if (responsibleId.HasValue)
        {
            query = query.Where(x => x.Payment.Responsibles.Any(r => r.UserId == responsibleId.Value));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x =>
                ((x.Payment.Invoice != null && x.Payment.Invoice.Number.ToLower().Contains(term)) || (x.Payment.Account != null && x.Payment.Account.ToLower().Contains(term))) ||
                (x.Payment.Description != null && x.Payment.Description.ToLower().Contains(term)) ||
                (x.Payment.Notes != null && x.Payment.Notes.ToLower().Contains(term)) ||
                (x.Payment.CounterpartyName != null && x.Payment.CounterpartyName.ToLower().Contains(term)) ||
                (x.Payment.Client != null && x.Payment.Client.Name != null && x.Payment.Client.Name.ToLower().Contains(term)) ||
                (x.Payment.Client != null && x.Payment.Client.Company != null && x.Payment.Client.Company.ToLower().Contains(term)) ||
                (x.Payment.ExpenseClient != null && x.Payment.ExpenseClient.Name != null && x.Payment.ExpenseClient.Name.ToLower().Contains(term)) ||
                (x.Payment.ExpenseClient != null && x.Payment.ExpenseClient.Company != null && x.Payment.ExpenseClient.Company.ToLower().Contains(term)) ||
                (x.Payment.ClientCase != null && x.Payment.ClientCase.Title != null && x.Payment.ClientCase.Title.ToLower().Contains(term)) ||
                x.Payment.InvoiceClientCases.Any(link => link.ClientCase != null && link.ClientCase.Title != null && link.ClientCase.Title.ToLower().Contains(term)) ||
                x.Payment.Acts.Any(act =>
                    act.Number.ToLower().Contains(term) ||
                    (act.Title != null && act.Title.ToLower().Contains(term)) ||
                    (act.InvoiceNumber != null && act.InvoiceNumber.ToLower().Contains(term)) ||
                    (act.CounterpartyInn != null && act.CounterpartyInn.ToLower().Contains(term)) ||
                    (act.Comment != null && act.Comment.ToLower().Contains(term)) ||
                    (act.Client != null && act.Client.Name != null && act.Client.Name.ToLower().Contains(term))) ||
                (x.Act != null && x.Act.Number != null && x.Act.Number.ToLower().Contains(term)) ||
                (x.Act != null && x.Act.Title != null && x.Act.Title.ToLower().Contains(term)) ||
                (x.Act != null && x.Act.InvoiceNumber != null && x.Act.InvoiceNumber.ToLower().Contains(term)) ||
                (x.Act != null && x.Act.CounterpartyInn != null && x.Act.CounterpartyInn.ToLower().Contains(term)) ||
                (x.Act != null && x.Act.Comment != null && x.Act.Comment.ToLower().Contains(term)) ||
                (x.Act != null && x.Act.Client != null && x.Act.Client.Name != null && x.Act.Client.Name.ToLower().Contains(term)));
        }

        return query;
    }

    private IQueryable<InvoiceQueryItem> ApplyRelatedDocumentFilters(
        IQueryable<InvoiceQueryItem> query,
        int? actId,
        int? paymentId)
    {
        if (actId.HasValue)
        {
            query = query.Where(x =>
                _db.Acts.Any(act =>
                    act.Id == actId.Value
                    && (act.InvoiceId == x.Payment.Id
                        || (!act.InvoiceId.HasValue
                            && act.InvoiceNumber != null
                            && act.InvoiceNumber == (x.Payment.Invoice != null ? x.Payment.Invoice.Number : x.Payment.Account)))));
        }

        if (paymentId.HasValue)
        {
            query = query.Where(x =>
                x.Payment.Id == paymentId.Value
                || _db.Payments.Any(payment =>
                    payment.Id == paymentId.Value
                    && payment.InvoiceId.HasValue
                    && x.Payment.InvoiceId == payment.InvoiceId.Value));
        }

        return query;
    }

    private static string? NormalizeClientRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "counterparty" or "client" or "supplier" => "counterparty",
            "expenseclient" or "expense-client" or "expense_client" => "expenseClient",
            _ => null,
        };
    }

    private static HashSet<int> ParseIntList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    private static IQueryable<InvoiceQueryItem> ApplySort(
        IQueryable<InvoiceQueryItem> query,
        string? sortBy,
        string? sortDir)
    {
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy?.ToLowerInvariant()) switch
        {
            "number" => desc
                ? query.OrderByDescending(x => x.Payment.Invoice != null ? x.Payment.Invoice.Number : x.Payment.Account)
                : query.OrderBy(x => x.Payment.Invoice != null ? x.Payment.Invoice.Number : x.Payment.Account),
            "amount" => desc
                ? query.OrderByDescending(x => x.Payment.Amount)
                : query.OrderBy(x => x.Payment.Amount),
            "status" => desc
                ? query.OrderByDescending(x => x.Payment.Status)
                : query.OrderBy(x => x.Payment.Status),
            "client" or "counterparty" => desc
                ? query.OrderByDescending(x => x.Payment.Client != null ? x.Payment.Client.Name : string.Empty)
                : query.OrderBy(x => x.Payment.Client != null ? x.Payment.Client.Name : string.Empty),
            "responsible" => desc
                ? query.OrderByDescending(x => x.Act != null && x.Act.Responsible != null
                    ? (x.Act.Responsible.FullName ?? x.Act.Responsible.Email ?? string.Empty)
                    : string.Empty)
                : query.OrderBy(x => x.Act != null && x.Act.Responsible != null
                    ? (x.Act.Responsible.FullName ?? x.Act.Responsible.Email ?? string.Empty)
                    : string.Empty),
            "duedate" => desc
                ? query.OrderByDescending(x => x.Payment.Date)
                : query.OrderBy(x => x.Payment.Date),
            "createdat" => desc
                ? query.OrderByDescending(x => x.Payment.CreatedAt)
                : query.OrderBy(x => x.Payment.CreatedAt),
            _ => desc
                ? query.OrderByDescending(x => x.Payment.Invoice != null ? x.Payment.Invoice.Date : (x.Payment.AccountDate ?? x.Payment.Date))
                : query.OrderBy(x => x.Payment.Invoice != null ? x.Payment.Invoice.Date : (x.Payment.AccountDate ?? x.Payment.Date)),
        };
    }

    private static IEnumerable<InvoiceDto> ApplyInvoiceSort(
        IEnumerable<InvoiceDto> items,
        string? sortBy,
        string? sortDir)
    {
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy?.ToLowerInvariant()) switch
        {
            "number" => desc
                ? items.OrderByDescending(x => x.Number, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(x => x.Number, StringComparer.OrdinalIgnoreCase),
            "amount" => desc
                ? items.OrderByDescending(x => x.Amount)
                : items.OrderBy(x => x.Amount),
            "status" => desc
                ? items.OrderByDescending(x => x.Status)
                : items.OrderBy(x => x.Status),
            "client" or "counterparty" => desc
                ? items.OrderByDescending(x => x.ClientName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(x => x.ClientName ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "responsible" => desc
                ? items.OrderByDescending(x => x.ResponsibleName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(x => x.ResponsibleName ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "duedate" => desc
                ? items.OrderByDescending(x => x.DueDate ?? DateTime.MinValue)
                : items.OrderBy(x => x.DueDate ?? DateTime.MaxValue),
            "createdat" => desc
                ? items.OrderByDescending(x => x.CreatedAt)
                : items.OrderBy(x => x.CreatedAt),
            _ => desc
                ? items.OrderByDescending(x => x.Date)
                : items.OrderBy(x => x.Date),
        };
    }

    private static void ApplyPaidFlags(Payment payment, DateTime? paidDate, DateTime today)
    {
        var effectiveDate = PaymentDomainService.NormalizeLocalDate(
            paidDate ?? payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date);

        if (payment.Status == PaymentStatus.Completed)
        {
            payment.PaidAmount = payment.Amount;
            payment.LastPaymentDate = effectiveDate;
            payment.PaidDate = effectiveDate;
            payment.IsPaid = true;
        }
        else if (payment.Status == PaymentStatus.Processing)
        {
            payment.PaidAmount = Math.Max(0m, Math.Min(payment.PaidAmount, payment.Amount));
            payment.IsPaid = false;
            if (payment.PaidAmount <= 0m)
            {
                payment.Status = payment.Date.Date < today.Date
                    ? PaymentStatus.Overdue
                    : PaymentStatus.Pending;
                payment.LastPaymentDate = null;
                payment.PaidDate = null;
            }
            else
            {
                payment.LastPaymentDate ??= effectiveDate;
                payment.PaidDate ??= effectiveDate;
            }
        }
        else
        {
            payment.PaidAmount = 0m;
            payment.LastPaymentDate = null;
            payment.PaidDate = null;
            payment.IsPaid = false;
        }

        PaymentDomainService.Normalize(payment);
    }

    private static string? NormalizeInvoiceWorkflowStatusOrNull(string? value)
        => InvoiceWorkflowStatus.Normalize(value);

    private static bool IsEditableInvoiceWorkflowStatus(string value)
        => value is InvoiceWorkflowStatus.Draft
            or InvoiceWorkflowStatus.NotSigned
            or InvoiceWorkflowStatus.RequiresClientSignature
            or InvoiceWorkflowStatus.OnApproval
            or InvoiceWorkflowStatus.Approved
            or InvoiceWorkflowStatus.ApprovalRejected
            or InvoiceWorkflowStatus.NeedsCorrection
            or InvoiceWorkflowStatus.OnSigning
            or InvoiceWorkflowStatus.SignedByClient
            or InvoiceWorkflowStatus.Cancelled;

    private static string? ResolveInvoiceWorkflowStatus(UpsertInvoiceRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkflowStatus))
        {
            var normalized = InvoiceWorkflowStatus.Normalize(request.WorkflowStatus);
            return normalized is not null && IsEditableInvoiceWorkflowStatus(normalized)
                ? normalized
                : null;
        }

        return InvoiceWorkflowStatus.FromPaymentStatus(request.Status, 0m, request.Amount);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static string? BuildLegacyGroupKey(
        string? accountNumber,
        DateTime accountDate,
        int? clientId,
        int? clientCaseId,
        PaymentType type)
    {
        var normalizedAccount = Normalize(accountNumber);
        if (normalizedAccount is null || !clientId.HasValue || clientId.Value <= 0)
        {
            return null;
        }

        return $"{normalizedAccount}::{accountDate.Year}::client:{clientId.Value}::case:{clientCaseId?.ToString() ?? "none"}::type:{type}";
    }

    private static decimal ResolvePaidAmount(Payment payment)
        => ResolvePaidAmount(payment.Amount, payment.PaidAmount, payment.Status);

    private static decimal ResolveOutstandingAmount(Payment payment)
        => ResolveOutstandingAmount(payment.Amount, payment.PaidAmount, payment.Status);

    private static decimal ResolvePaidAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        var normalizedAmount = NormalizeMoneyAmount(amount);
        var normalizedPaidAmount = Math.Min(NormalizeMoneyAmount(paidAmount), normalizedAmount);
        if (normalizedPaidAmount > 0m)
        {
            return normalizedPaidAmount;
        }

        return status == PaymentStatus.Completed ? normalizedAmount : 0m;
    }

    private static decimal ResolveOutstandingAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        if (status is PaymentStatus.Completed or PaymentStatus.Cancelled)
        {
            return 0m;
        }

        var normalizedAmount = NormalizeMoneyAmount(amount);
        var resolvedPaidAmount = ResolvePaidAmount(normalizedAmount, paidAmount, status);
        return NormalizeMoneyAmount(Math.Max(normalizedAmount - resolvedPaidAmount, 0m));
    }

    private static decimal ResolveOutstandingInvoiceSummaryAmount(InvoiceDto invoice)
        => invoice.OutstandingAmount > 0m
            ? invoice.OutstandingAmount
            : ResolveOutstandingAmount(invoice.Amount, invoice.PaidAmount, invoice.Status);

    private static decimal ResolvePaidInvoiceSummaryAmount(InvoiceDto invoice)
        => invoice.PaidAmount > 0m
            ? invoice.PaidAmount
            : ResolvePaidAmount(invoice.Amount, invoice.PaidAmount, invoice.Status);

    private static decimal NormalizeMoneyAmount(decimal amount)
        => Math.Round(Math.Max(amount, 0m), 2, MidpointRounding.AwayFromZero);

    private static DateTime EffectivePlannedDate(Payment payment)
        => payment.PlannedDate == default ? payment.Date : payment.PlannedDate;

    private static string BuildInvoiceMigrationKey(string number, int year, int clientId, int? clientCaseId, PaymentType type)
        => $"{number.Trim().ToLowerInvariant()}::{year}::client:{clientId}::case:{clientCaseId?.ToString() ?? "none"}::type:{type}";

    private static InvoiceMigrationBinding? TryBuildInvoiceMigrationBinding(Payment payment, bool preferCanonicalInvoice)
    {
        var clientId = payment.ClientId;
        if (!clientId.HasValue || clientId.Value <= 0)
        {
            return null;
        }

        var rawNumber = preferCanonicalInvoice && payment.Invoice != null
            ? payment.Invoice.Number
            : (payment.LegacyAccountNumber ?? payment.Account);
        var number = Normalize(rawNumber);
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var rawDate = preferCanonicalInvoice && payment.Invoice != null
            ? payment.Invoice.Date
            : (payment.LegacyAccountDate ?? payment.AccountDate ?? payment.Date);
        var invoiceDate = rawDate.Date;
        var clientCaseId = payment.ClientCaseId is > 0 ? payment.ClientCaseId : null;

        return new InvoiceMigrationBinding(
            payment,
            BuildInvoiceMigrationKey(number, invoiceDate.Year, clientId.Value, clientCaseId, payment.Type),
            number,
            invoiceDate,
            clientId.Value,
            clientCaseId,
            payment.Type);
    }

    private static void IncrementNestedCount<TKeyOuter, TKeyInner>(
        IDictionary<TKeyOuter, Dictionary<TKeyInner, int>> target,
        TKeyOuter outerKey,
        TKeyInner innerKey)
        where TKeyOuter : notnull
        where TKeyInner : notnull
    {
        if (!target.TryGetValue(outerKey, out var bucket))
        {
            bucket = new Dictionary<TKeyInner, int>();
            target[outerKey] = bucket;
        }

        bucket[innerKey] = bucket.TryGetValue(innerKey, out var count) ? count + 1 : 1;
    }

    private static bool SyncInvoiceMigrationCaseLinks(Payment payment, int clientId, int? clientCaseId)
    {
        var changed = false;

        if (payment.ClientId != clientId)
        {
            payment.ClientId = clientId;
            changed = true;
        }

        if (payment.ClientCaseId != clientCaseId)
        {
            payment.ClientCaseId = clientCaseId;
            changed = true;
        }

        if (clientCaseId.HasValue)
        {
            var toRemove = payment.InvoiceClientCases
                .Where(link => link.ClientCaseId != clientCaseId.Value)
                .ToList();
            foreach (var link in toRemove)
            {
                payment.InvoiceClientCases.Remove(link);
                changed = true;
            }

            if (!payment.InvoiceClientCases.Any(link => link.ClientCaseId == clientCaseId.Value))
            {
                payment.InvoiceClientCases.Add(new InvoiceClientCase
                {
                    PaymentId = payment.Id,
                    ClientCaseId = clientCaseId.Value,
                });
                changed = true;
            }
        }
        else if (payment.InvoiceClientCases.Count > 0)
        {
            payment.InvoiceClientCases.Clear();
            changed = true;
        }

        return changed;
    }

    private sealed record InvoiceMigrationBinding(
        Payment Payment,
        string Key,
        string Number,
        DateTime InvoiceDate,
        int ClientId,
        int? ClientCaseId,
        PaymentType Type);

    private static List<int> NormalizeIds(IEnumerable<int?> ids)
        => ids.Where(x => x.HasValue && x.Value > 0).Select(x => x!.Value).Distinct().ToList();

    private static List<int> NormalizeInvoiceClientCaseIds(UpsertInvoiceRequest request)
    {
        var ids = request.ClientCaseIds?.Select(x => (int?)x).ToList() ?? new List<int?>();
        if (request.ClientCaseId.HasValue)
        {
            ids.Add(request.ClientCaseId.Value);
        }

        return NormalizeIds(ids);
    }

    private static List<int> NormalizeInvoiceActIds(UpsertInvoiceRequest request)
    {
        var ids = request.ActIds?.Select(x => (int?)x).ToList() ?? new List<int?>();
        if (request.ActId.HasValue)
        {
            ids.Add(request.ActId.Value);
        }

        return NormalizeIds(ids);
    }

    private async Task<ActionResult?> ValidateContractClientLinkAsync(int? contractId, int? clientId, int? existingInvoiceId, CancellationToken ct)
    {
        if (!contractId.HasValue) return null;
        if (!clientId.HasValue || clientId.Value <= 0) return BadRequest("Contract can be linked only when a client counterparty is selected.");

        var contractStatus = await _db.Contracts.AsNoTracking()
            .Where(c => c.Id == contractId.Value)
            .Select(c => c.Status)
            .FirstOrDefaultAsync(ct);
        if (contractStatus is null) return BadRequest($"Unknown contractId {contractId.Value}");

        if (string.Equals(contractStatus, ContractStatus.Terminated, StringComparison.OrdinalIgnoreCase))
        {
            var keepsExistingTerminatedContract = existingInvoiceId.HasValue
                && await _db.Payments.AsNoTracking()
                    .AnyAsync(payment => payment.Id == existingInvoiceId.Value && payment.ContractId == contractId.Value, ct);
            if (!keepsExistingTerminatedContract)
            {
                return BadRequest("Нельзя создать новый счет или привязать счет к расторгнутому договору.");
            }
        }

        var linkExists = await _db.Set<ClientContract>().AsNoTracking()
            .AnyAsync(x => x.ContractId == contractId.Value && x.ClientId == clientId.Value, ct);
        if (!linkExists) return BadRequest("Client is not linked to selected contract.");

        return null;
    }

    private async Task<ActionResult?> ValidateExpenseClientAsync(UpsertInvoiceRequest request, CancellationToken ct)
    {
        if (request.Type != PaymentType.Expense)
        {
            return null;
        }

        var hasExpenseClient = request.ExpenseClientId.HasValue && request.ExpenseClientId.Value > 0;
        var hasExpenseLegalEntity = request.ExpenseLegalEntityId.HasValue && request.ExpenseLegalEntityId.Value > 0;
        if (!hasExpenseClient && !hasExpenseLegalEntity)
        {
            return null;
        }

        return await ValidateCounterpartyAsync(request.ExpenseClientId, request.ExpenseLegalEntityId, "expense", ct);
    }

    private static int? ResolveInvoiceExpenseClientId(UpsertInvoiceRequest request)
        => request.Type == PaymentType.Expense && request.ExpenseClientId.HasValue && request.ExpenseClientId.Value > 0
            ? request.ExpenseClientId.Value
            : null;

    private static int? ResolveInvoiceExpenseLegalEntityId(UpsertInvoiceRequest request)
        => request.Type == PaymentType.Expense && request.ExpenseLegalEntityId.HasValue && request.ExpenseLegalEntityId.Value > 0
            ? request.ExpenseLegalEntityId.Value
            : null;

    private sealed record CounterpartyOwner(int? ClientId, int? LegalEntityId);

    private static CounterpartyOwner ResolveInvoiceCaseOwner(UpsertInvoiceRequest request)
        => request.Type == PaymentType.Expense
            ? new CounterpartyOwner(ResolveInvoiceExpenseClientId(request) ?? request.ClientId, ResolveInvoiceExpenseLegalEntityId(request) ?? request.LegalEntityId)
            : new CounterpartyOwner(request.ClientId, request.LegalEntityId);

    private async Task<ActionResult?> ValidateCounterpartyAsync(int? clientId, int? legalEntityId, string fieldName, CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest($"{fieldName}: specify exactly one of clientId or legalEntityId.");
        }

        if (hasClient)
        {
            var exists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId!.Value, ct);
            var clientFieldName = string.Equals(fieldName, "expense", StringComparison.OrdinalIgnoreCase)
                ? "expenseClientId"
                : "clientId";
            return exists ? null : BadRequest($"Unknown {clientFieldName} {clientId}");
        }

        var legalExists = await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Id == legalEntityId!.Value, ct);
        var legalEntityFieldName = string.Equals(fieldName, "expense", StringComparison.OrdinalIgnoreCase)
            ? "expenseLegalEntityId"
            : "legalEntityId";
        return legalExists ? null : BadRequest($"Unknown {legalEntityFieldName} {legalEntityId}");
    }

    private async Task<(ActionResult? Error, List<int> ClientCaseIds)> ValidateInvoiceClientCasesAsync(int? clientId, int? legalEntityId, UpsertInvoiceRequest request, CancellationToken ct)
    {
        var clientCaseIds = NormalizeInvoiceClientCaseIds(request);
        if (clientCaseIds.Count == 0)
        {
            return (null, clientCaseIds);
        }

        var rules = await GetInvoiceRulesSettingsAsync(ct);
        if (clientCaseIds.Count > rules.MaxClientCasesPerInvoice)
        {
            return (BadRequest($"В счёте можно указать не более {rules.MaxClientCasesPerInvoice} дела(дел)."), new List<int>());
        }

        var cases = await _db.ClientCases
            .AsNoTracking()
            .Where(x => clientCaseIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ClientId, x.LegalEntityId })
            .ToListAsync(ct);

        if (cases.Count != clientCaseIds.Count)
        {
            var existingIds = cases.Select(x => x.Id).ToHashSet();
            var missingIds = clientCaseIds.Where(id => !existingIds.Contains(id));
            return (BadRequest($"Unknown clientCaseIds: {string.Join(", ", missingIds)}"), new List<int>());
        }

        var foreignClientCaseIds = cases
            .Where(x => x.ClientId != clientId || x.LegalEntityId != legalEntityId)
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToList();

        if (foreignClientCaseIds.Count > 0)
        {
            return (BadRequest($"Дела не связаны с выбранным клиентом или юрлицом: {string.Join(", ", foreignClientCaseIds)}"), new List<int>());
        }

        return (null, clientCaseIds);
    }

    private async Task<(ActionResult? Error, List<int> ActIds)> ValidateInvoiceActsAsync(
        int? clientId,
        int? legalEntityId,
        UpsertInvoiceRequest request,
        int? invoicePaymentId,
        int? invoiceEntityId,
        CancellationToken ct)
    {
        var actIds = NormalizeInvoiceActIds(request);
        if (actIds.Count == 0)
        {
            return (null, actIds);
        }

        var rules = await GetInvoiceRulesSettingsAsync(ct);
        if (actIds.Count > rules.MaxActsPerInvoice)
        {
            return (BadRequest($"В счёте можно указать не более {rules.MaxActsPerInvoice} акта(ов)."), new List<int>());
        }

        var acts = await _db.Acts
            .AsNoTracking()
            .Where(x => actIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ClientId, x.LegalEntityId, x.InvoiceId })
            .ToListAsync(ct);

        if (acts.Count != actIds.Count)
        {
            var existingIds = acts.Select(x => x.Id).ToHashSet();
            var missingIds = actIds.Where(id => !existingIds.Contains(id));
            return (BadRequest($"Unknown actIds: {string.Join(", ", missingIds)}"), new List<int>());
        }

        var allowedInvoicePaymentIds = await ResolveAllowedInvoiceActPaymentIdsAsync(invoicePaymentId, invoiceEntityId, ct);

        var foreignClientActIds = acts
            .Where(x => !(x.InvoiceId.HasValue && allowedInvoicePaymentIds.Contains(x.InvoiceId.Value))
                && (x.ClientId != clientId || x.LegalEntityId != legalEntityId))
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToList();

        if (foreignClientActIds.Count > 0)
        {
            return (BadRequest($"Акты не связаны с выбранным клиентом или юрлицом: {string.Join(", ", foreignClientActIds)}"), new List<int>());
        }

        var linkedToOtherInvoices = acts
            .Where(x => x.InvoiceId.HasValue && !allowedInvoicePaymentIds.Contains(x.InvoiceId.Value))
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToList();

        if (linkedToOtherInvoices.Count > 0)
        {
            return (BadRequest($"Акты уже привязаны к другому счету: {string.Join(", ", linkedToOtherInvoices)}"), new List<int>());
        }

        return (null, actIds);
    }

    private async Task<HashSet<int>> ResolveAllowedInvoiceActPaymentIdsAsync(int? invoicePaymentId, int? invoiceEntityId, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        if (invoicePaymentId.HasValue && invoicePaymentId.Value > 0)
        {
            ids.Add(invoicePaymentId.Value);
        }

        if (invoiceEntityId.HasValue && invoiceEntityId.Value > 0)
        {
            var siblingPaymentIds = await _db.Payments
                .AsNoTracking()
                .Where(payment => payment.InvoiceId == invoiceEntityId.Value)
                .Select(payment => payment.Id)
                .ToListAsync(ct);
            ids.UnionWith(siblingPaymentIds);
        }

        return ids;
    }

    private static readonly Expression<Func<InvoiceQueryItem, InvoiceProjectionRaw>> ProjectionRaw = x => new InvoiceProjectionRaw
    {
        Id = x.Payment.Id,
        InvoiceEntityId = x.Payment.InvoiceId,
        Number = x.Payment.Invoice != null ? x.Payment.Invoice.Number : (x.Payment.Account ?? string.Empty),
        Date = x.Payment.Invoice != null ? x.Payment.Invoice.Date : (x.Payment.AccountDate ?? x.Payment.Date),
        DueDate = x.Payment.Date,
        Amount = x.Payment.Amount,
        PaidAmount = x.Payment.PaidAmount,
        Type = x.Payment.Type,
        Status = x.Payment.Status,
        WorkflowStatus = x.Payment.InvoiceWorkflowStatus,
        SentForSigningAt = x.Payment.InvoiceSentForSigningAt,
        SignedAt = x.Payment.InvoiceSignedAt,
        CancelledAt = x.Payment.InvoiceCancelledAt,
        CancelledByUserId = x.Payment.InvoiceCancelledByUserId,
        CancelledReason = x.Payment.InvoiceCancelledReason,
        IsPaid = x.Payment.IsPaid,
        PaidDate = x.Payment.PaidDate,
        ClientId = x.Payment.ClientId,
        LegalEntityId = x.Payment.LegalEntityId,
        ExpenseClientId = x.Payment.ExpenseClientId,
        ExpenseLegalEntityId = x.Payment.ExpenseLegalEntityId,
        ContractId = x.Payment.ContractId,
        ClientName = x.Payment.Client != null ? x.Payment.Client.Name : null,
        LegalEntityName = x.Payment.LegalEntity != null ? x.Payment.LegalEntity.ShortName : null,
        LegalEntityInn = x.Payment.LegalEntity != null ? x.Payment.LegalEntity.Inn : null,
        ClientCompany = x.Payment.Client != null ? x.Payment.Client.Company : null,
        ClientStatusId = x.Payment.Client != null ? x.Payment.Client.ClientStatusId : null,
        ClientStatus = x.Payment.Client != null && x.Payment.Client.ClientStatus != null
            ? new ClientStatusDto
            {
                Id = x.Payment.Client.ClientStatus.Id,
                Name = x.Payment.Client.ClientStatus.Name,
                Description = x.Payment.Client.ClientStatus.Description,
                ColorHex = x.Payment.Client.ClientStatus.ColorHex,
                IsActive = x.Payment.Client.ClientStatus.IsActive,
                CreatedAt = x.Payment.Client.ClientStatus.CreatedAt,
                SystemKey = x.Payment.Client.ClientStatus.SystemKey,
            }
            : null,
        ExpenseClientName = x.Payment.ExpenseClient != null ? x.Payment.ExpenseClient.Name : null,
        ExpenseLegalEntityName = x.Payment.ExpenseLegalEntity != null ? x.Payment.ExpenseLegalEntity.ShortName : null,
        ExpenseLegalEntityInn = x.Payment.ExpenseLegalEntity != null ? x.Payment.ExpenseLegalEntity.Inn : null,
        ExpenseClientCompany = x.Payment.ExpenseClient != null ? x.Payment.ExpenseClient.Company : null,
        ExpenseClientStatusId = x.Payment.ExpenseClient != null ? x.Payment.ExpenseClient.ClientStatusId : null,
        ExpenseClientStatus = x.Payment.ExpenseClient != null && x.Payment.ExpenseClient.ClientStatus != null
            ? new ClientStatusDto
            {
                Id = x.Payment.ExpenseClient.ClientStatus.Id,
                Name = x.Payment.ExpenseClient.ClientStatus.Name,
                Description = x.Payment.ExpenseClient.ClientStatus.Description,
                ColorHex = x.Payment.ExpenseClient.ClientStatus.ColorHex,
                IsActive = x.Payment.ExpenseClient.ClientStatus.IsActive,
                CreatedAt = x.Payment.ExpenseClient.ClientStatus.CreatedAt,
                SystemKey = x.Payment.ExpenseClient.ClientStatus.SystemKey,
            }
            : null,
        ClientCaseId = x.Payment.ClientCaseId,
        ClientCaseTitle = x.Payment.ClientCase != null ? x.Payment.ClientCase.Title : null,
        ClientCaseReference = x.Payment.ClientCaseReference,
        Description = x.Payment.Description,
        IsNotForAccounting = x.Payment.IsNotForAccounting,
        ActReference = x.Payment.Notes,
        ActId = x.Act != null ? x.Act.Id : (int?)null,
        ActNumber = x.Act != null ? x.Act.Number : null,
        ActTitle = x.Act != null ? x.Act.Title : null,
        ActStatus = x.Act != null ? x.Act.Status : null,
        ActWorkflowStatus = x.Act != null ? x.Act.WorkflowStatus : null,
        ResponsibleId = x.Act != null ? x.Act.ResponsibleId : null,
        ResponsibleName = x.Act != null && x.Act.Responsible != null
            ? (string.IsNullOrWhiteSpace(x.Act.Responsible.FullName)
                ? x.Act.Responsible.Email
                : x.Act.Responsible.FullName)
            : null,
        CounterpartyInn = x.Act != null ? x.Act.CounterpartyInn : null,
        CounterpartyName = x.Payment.CounterpartyName,
        ServiceId = x.Payment.ServiceId,
        ServiceName = x.Payment.Service != null ? x.Payment.Service.Name : null,
        PaymentStatusName = x.Payment.PaymentStatusEntity != null ? x.Payment.PaymentStatusEntity.Name : null,
        CreatedAt = x.Payment.CreatedAt,
        AuditCreatedByUserId = EF.Property<int?>(x.Payment, "CreatedByUserId"),
        AuditCreatedByUserName = EF.Property<string?>(x.Payment, "CreatedByUserName"),
        AuditUpdatedAt = EF.Property<DateTime?>(x.Payment, "UpdatedAt"),
        AuditUpdatedByUserId = EF.Property<int?>(x.Payment, "UpdatedByUserId"),
        AuditUpdatedByUserName = EF.Property<string?>(x.Payment, "UpdatedByUserName"),
        AuditIsDeleted = EF.Property<bool>(x.Payment, "IsDeleted"),
        AuditDeletedAt = EF.Property<DateTime?>(x.Payment, "DeletedAt"),
        AuditDeletedByUserId = EF.Property<int?>(x.Payment, "DeletedByUserId"),
        AuditDeletedByUserName = EF.Property<string?>(x.Payment, "DeletedByUserName"),
        DocumentKind = x.Payment.DocumentKind,
        SystemNotes = x.Payment.SystemNotes,
    };

    private static InvoiceDto MapFromRaw(InvoiceProjectionRaw raw)
    {
        return new InvoiceDto
        {
            Id = raw.Id,
            InvoiceEntityId = raw.InvoiceEntityId,
            Number = raw.Number,
            Date = PaymentDomainService.NormalizeLocalDate(raw.Date),
            DueDate = PaymentDomainService.NormalizeLocalDate(raw.DueDate),
            Amount = raw.Amount,
            PaidAmount = ResolvePaidAmount(raw.Amount, raw.PaidAmount, raw.Status),
            OutstandingAmount = ResolveOutstandingAmount(raw.Amount, raw.PaidAmount, raw.Status),
            Type = raw.Type,
            Status = raw.Status,
            WorkflowStatus = raw.WorkflowStatus,
            SentForSigningAt = PaymentDomainService.NormalizeUtcInstant(raw.SentForSigningAt),
            SignedAt = PaymentDomainService.NormalizeUtcInstant(raw.SignedAt),
            CancelledAt = PaymentDomainService.NormalizeUtcInstant(raw.CancelledAt),
            CancelledByUserId = raw.CancelledByUserId,
            CancelledReason = raw.CancelledReason,
            IsPaid = raw.IsPaid,
            PaidDate = PaymentDomainService.NormalizeLocalDate(raw.PaidDate),
            ClientId = raw.ClientId,
            LegalEntityId = raw.LegalEntityId,
            ExpenseClientId = raw.ExpenseClientId,
            ExpenseLegalEntityId = raw.ExpenseLegalEntityId,
            ContractId = raw.ContractId,
            ClientName = raw.ClientName,
            LegalEntityName = raw.LegalEntityName,
            LegalEntityInn = raw.LegalEntityInn,
            ClientCompany = raw.ClientCompany,
            ClientStatusId = raw.ClientStatusId,
            ClientStatus = raw.ClientStatus,
            ExpenseClientName = raw.ExpenseClientName,
            ExpenseLegalEntityName = raw.ExpenseLegalEntityName,
            ExpenseLegalEntityInn = raw.ExpenseLegalEntityInn,
            ExpenseClientCompany = raw.ExpenseClientCompany,
            ExpenseClientStatusId = raw.ExpenseClientStatusId,
            ExpenseClientStatus = raw.ExpenseClientStatus,
            ClientCaseId = raw.ClientCaseId,
            ClientCaseTitle = raw.ClientCaseTitle,
            ClientCaseReference = raw.ClientCaseReference,
            Description = raw.Description,
            IsNotForAccounting = raw.IsNotForAccounting,
            ActReference = raw.ActReference,
            ActId = raw.ActId,
            ActNumber = raw.ActNumber,
            ActTitle = raw.ActTitle,
            ActStatus = raw.ActStatus,
            ActWorkflowStatus = raw.ActWorkflowStatus,
            ResponsibleId = raw.ResponsibleId,
            ResponsibleName = raw.ResponsibleName,
            CounterpartyInn = raw.CounterpartyInn,
            CounterpartyName = raw.CounterpartyName,
            ServiceId = raw.ServiceId,
            ServiceName = raw.ServiceName,
            PaymentStatusName = raw.PaymentStatusName,
            CreatedAt = PaymentDomainService.NormalizeUtcInstant(raw.CreatedAt),
            AuditCreatedByUserId = raw.AuditCreatedByUserId,
            AuditCreatedByUserName = raw.AuditCreatedByUserName,
            AuditUpdatedAt = PaymentDomainService.NormalizeUtcInstant(raw.AuditUpdatedAt),
            AuditUpdatedByUserId = raw.AuditUpdatedByUserId,
            AuditUpdatedByUserName = raw.AuditUpdatedByUserName,
            AuditIsDeleted = raw.AuditIsDeleted,
            AuditDeletedAt = PaymentDomainService.NormalizeUtcInstant(raw.AuditDeletedAt),
            AuditDeletedByUserId = raw.AuditDeletedByUserId,
            AuditDeletedByUserName = raw.AuditDeletedByUserName,
            DocumentKind = raw.DocumentKind,
            LineItems = InvoiceLineItemStorage.ExtractFromSystemNotes(raw.SystemNotes),
        };
    }

    private async Task<InvoiceDto> LoadInvoiceDtoAsync(int paymentId, CancellationToken ct)
    {
        var raw = await BuildInvoiceQuery()
            .Where(x => x.Payment.Id == paymentId)
            .Select(ProjectionRaw)
            .FirstAsync(ct);

        var dto = MapFromRaw(raw);
        await EnrichInvoiceDtosAsync(new List<InvoiceDto> { dto }, ct);
        return dto;
    }

    private static void ApplyPersistedPaymentState(InvoiceDto dto, Payment payment)
    {
        dto.Status = payment.Status;
        dto.WorkflowStatus = payment.InvoiceWorkflowStatus;
        dto.SentForSigningAt = PaymentDomainService.NormalizeUtcInstant(payment.InvoiceSentForSigningAt);
        dto.SignedAt = PaymentDomainService.NormalizeUtcInstant(payment.InvoiceSignedAt);
        dto.CancelledAt = PaymentDomainService.NormalizeUtcInstant(payment.InvoiceCancelledAt);
        dto.CancelledByUserId = payment.InvoiceCancelledByUserId;
        dto.CancelledReason = payment.InvoiceCancelledReason;
        dto.IsPaid = payment.IsPaid;
        dto.PaidDate = PaymentDomainService.NormalizeLocalDate(payment.PaidDate);
        dto.PaidAmount = ResolvePaidAmount(payment);
        dto.OutstandingAmount = ResolveOutstandingAmount(payment);
    }

    private async Task<InvoiceDto> SaveAndReloadInvoiceAsync(Payment payment, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        var dto = await LoadInvoiceDtoAsync(payment.Id, ct);
        ApplyPersistedPaymentState(dto, payment);
        return dto;
    }

    private void ApplyInvoiceWorkflowStatus(Payment payment, string workflowStatus, DateTime now, string? reason)
    {
        payment.InvoiceWorkflowStatus = workflowStatus;

        switch (workflowStatus)
        {
            case InvoiceWorkflowStatus.Draft:
            case InvoiceWorkflowStatus.NotSigned:
            case InvoiceWorkflowStatus.RequiresClientSignature:
            case InvoiceWorkflowStatus.OnApproval:
            case InvoiceWorkflowStatus.Approved:
            case InvoiceWorkflowStatus.ApprovalRejected:
            case InvoiceWorkflowStatus.NeedsCorrection:
                payment.InvoiceSentForSigningAt = null;
                payment.InvoiceSignedAt = null;
                payment.InvoiceCancelledAt = null;
                payment.InvoiceCancelledByUserId = null;
                payment.InvoiceCancelledReason = null;
                break;
            case InvoiceWorkflowStatus.OnSigning:
                payment.InvoiceSentForSigningAt ??= now;
                payment.InvoiceSignedAt = null;
                payment.InvoiceCancelledAt = null;
                payment.InvoiceCancelledByUserId = null;
                payment.InvoiceCancelledReason = null;
                break;
            case InvoiceWorkflowStatus.SignedByClient:
                payment.InvoiceSentForSigningAt ??= now;
                payment.InvoiceSignedAt ??= now;
                payment.InvoiceCancelledAt = null;
                payment.InvoiceCancelledByUserId = null;
                payment.InvoiceCancelledReason = null;
                break;
            case InvoiceWorkflowStatus.Cancelled:
                payment.InvoiceCancelledAt ??= now;
                payment.InvoiceCancelledByUserId ??= User.GetUserId();
                payment.InvoiceCancelledReason ??= reason ?? "Быстрая смена статуса документа";
                break;
        }
    }

    private async Task SyncSiblingInvoiceWorkflowStatusesAsync(
        Payment payment,
        string workflowStatus,
        DateTime now,
        string? reason,
        CancellationToken ct)
    {
        var invoiceEntityId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (!invoiceEntityId.HasValue || invoiceEntityId.Value <= 0)
        {
            return;
        }

        var siblings = await _db.Payments
            .Where(candidate =>
                candidate.InvoiceId == invoiceEntityId.Value
                && candidate.Id != payment.Id
                && candidate.DocumentKind == DocumentKind.Invoice)
            .ToListAsync(ct);

        foreach (var sibling in siblings)
        {
            ApplyInvoiceWorkflowStatus(sibling, workflowStatus, now, reason);
        }
    }

    private async Task<List<InvoiceDto>> QueryInvoiceDtosAsync(
        DateTime? from,
        DateTime? to,
        int? clientId,
        int? legalEntityId,
        IReadOnlySet<int>? clientIds,
        int? contractId,
        int? actId,
        int? paymentId,
        int? caseId,
        int? responsibleId,
        string? search,
        PaymentType? type,
        string? workflowStatus,
        string? clientRole,
        bool includeMetadata,
        CancellationToken ct,
        DataVisibilityAccess? visibility = null,
        bool installmentsOnly = false)
    {
        var query = BuildInvoiceQuery(visibility);
        query = ApplyFilters(query, from, to, null, clientId, legalEntityId, contractId, caseId, responsibleId, search, type, clientRole, clientIds);
        query = ApplyRelatedDocumentFilters(query, actId, paymentId);
        query = ApplyInstallmentsFilter(query, installmentsOnly);
        if (!string.IsNullOrWhiteSpace(workflowStatus))
        {
            query = query.Where(x => x.Payment.InvoiceWorkflowStatus == workflowStatus);
        }

        var rawItems = await query
            .Select(ProjectionRaw)
            .ToListAsync(ct);

        var items = rawItems.Select(MapFromRaw).ToList();
        if (includeMetadata)
        {
            await EnrichInvoiceDtosAsync(items, ct);
        }
        else
        {
            await ApplyEffectiveInvoiceStateAsync(items, ct);
        }

        return items;
    }

    private IQueryable<InvoiceQueryItem> ApplyInstallmentsFilter(
        IQueryable<InvoiceQueryItem> query,
        bool installmentsOnly)
    {
        if (!installmentsOnly)
        {
            return query;
        }

        return query.Where(item =>
            item.Payment.InvoiceId.HasValue
            && _db.Payments.Any(payment =>
                payment.InvoiceId == item.Payment.InvoiceId
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains(InstallmentPaymentMarker)));
    }

    private async Task EnrichInvoiceDtosAsync(List<InvoiceDto> items, CancellationToken ct)
    {
        await EnrichInvoiceMetadataAsync(items, ct);
        await ApplyEffectiveInvoiceStateAsync(items, ct);
    }

    private async Task EnrichInvoiceMetadataAsync(List<InvoiceDto> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(i => i.Id).Distinct().ToList();
        var responsiblesMap = await LoadResponsiblesMapAsync(ids, ct);
        var actsMap = await LoadActsMapAsync(ids, ct);
        var clientCasesMap = await LoadClientCasesMapAsync(ids, ct);
        var lineItemsMap = await LoadLineItemsMapAsync(ids, ct);

        foreach (var item in items)
        {
            item.Responsibles = responsiblesMap.GetValueOrDefault(item.Id, new List<InvoiceResponsibleDto>());
            if (item.Responsibles.Count > 0)
            {
                item.ResponsibleId = item.Responsibles[0].Id;
                item.ResponsibleName = item.Responsibles[0].FullName;
            }

            item.Acts = actsMap.GetValueOrDefault(item.Id, new List<InvoiceActDto>());
            item.ActIds = item.Acts.Select(x => x.Id).ToList();
            if (item.Acts.Count > 0 && !item.ActId.HasValue)
            {
                var primaryAct = item.Acts[0];
                item.ActId = primaryAct.Id;
                item.ActNumber = primaryAct.Number;
                item.ActTitle = primaryAct.Title;
                item.ActStatus = primaryAct.Status;
            }

            item.ClientCases = clientCasesMap.GetValueOrDefault(item.Id, new List<InvoiceClientCaseDto>());
            item.ClientCaseIds = item.ClientCases.Select(x => x.Id).ToList();
            if (item.ClientCases.Count > 0 && !item.ClientCaseId.HasValue)
            {
                var primaryCase = item.ClientCases[0];
                item.ClientCaseId = primaryCase.Id;
                item.ClientCaseTitle = primaryCase.Title;
            }

            if (lineItemsMap.TryGetValue(item.Id, out var storedLineItems) && storedLineItems.Count > 0)
            {
                item.LineItems = storedLineItems;
            }
        }
    }

    private async Task ApplyEffectiveInvoiceStateAsync(List<InvoiceDto> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var invoiceEntityIds = items
            .Where(item => item.InvoiceEntityId.HasValue && item.InvoiceEntityId.Value > 0)
            .Select(item => item.InvoiceEntityId!.Value)
            .Distinct()
            .ToList();

        var legacyGroupKeys = items
            .Select(item => BuildLegacyGroupKey(item.Number, item.Date, item.ClientId, item.ClientCaseId, item.Type))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var legacyAccountNumbers = items
            .Select(item => item.Number)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (invoiceEntityIds.Count == 0 && legacyGroupKeys.Count == 0)
        {
            return;
        }

        var candidatePayments = await _db.Payments
            .AsNoTracking()
            .Where(payment =>
                (payment.InvoiceId.HasValue && invoiceEntityIds.Contains(payment.InvoiceId.Value))
                || (!payment.InvoiceId.HasValue
                    && payment.Account != null
                    && legacyAccountNumbers.Contains(payment.Account.Trim().ToLower())))
            .ToListAsync(ct);

        var linkedPaymentsByInvoiceId = candidatePayments
            .Where(payment => payment.InvoiceId.HasValue && invoiceEntityIds.Contains(payment.InvoiceId.Value))
            .GroupBy(payment => payment.InvoiceId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var legacyPaymentsByGroupKey = candidatePayments
            .Where(payment => !payment.InvoiceId.HasValue)
            .Select(payment => new
            {
                Payment = payment,
                LegacyKey = BuildLegacyGroupKey(payment.Account, payment.AccountDate ?? payment.Date, payment.ClientId, payment.ClientCaseId, payment.Type),
            })
            .Where(entry => entry.LegacyKey is not null && legacyGroupKeys.Contains(entry.LegacyKey))
            .GroupBy(entry => entry.LegacyKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Payment).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var relatedPayments = new List<Payment>();

            if (item.InvoiceEntityId.HasValue
                && linkedPaymentsByInvoiceId.TryGetValue(item.InvoiceEntityId.Value, out var linkedPayments))
            {
                relatedPayments.AddRange(linkedPayments);
            }

            var legacyGroupKey = BuildLegacyGroupKey(item.Number, item.Date, item.ClientId, item.ClientCaseId, item.Type);
            if (!string.IsNullOrWhiteSpace(legacyGroupKey)
                && legacyPaymentsByGroupKey.TryGetValue(legacyGroupKey, out var legacyPayments))
            {
                relatedPayments.AddRange(legacyPayments);
            }

            if (relatedPayments.Count == 0)
            {
                continue;
            }

            relatedPayments = relatedPayments
                .GroupBy(payment => payment.Id)
                .Select(group => group.First())
                .ToList();

            var statusSourcePayments = InvoicePaymentStateService.SelectStatusSourcePayments(relatedPayments, item.Id, item.Amount);
            var sourceOutstandingAmount = 0m;
            if (statusSourcePayments.Count > 0)
            {
                item.PaidAmount = NormalizeMoneyAmount(statusSourcePayments.Sum(InvoicePaymentStateService.ResolvePaidAmount));
                sourceOutstandingAmount = NormalizeMoneyAmount(statusSourcePayments.Sum(InvoicePaymentStateService.ResolveOutstandingAmount));
                item.DueDate = statusSourcePayments
                    .Select(InvoicePaymentStateService.EffectivePlannedDate)
                    .Max();

                var paidDates = statusSourcePayments
                    .Where(payment => payment.PaidDate.HasValue)
                    .Select(payment => payment.PaidDate!.Value)
                    .ToList();
                if (paidDates.Count > 0)
                {
                    item.PaidDate = paidDates.Max();
                }
            }

            item.Status = InvoicePaymentStateService.DeriveInvoiceStatus(
                statusSourcePayments,
                item.Amount,
                item.Status,
                item.DueDate,
                now);
            item.IsPaid = item.Status == PaymentStatus.Completed;
            if (statusSourcePayments.Count > 0)
            {
                item.OutstandingAmount = ResolveEffectiveInvoiceOutstandingAmount(
                    item.Amount,
                    item.PaidAmount,
                    item.Status,
                    sourceOutstandingAmount,
                    relatedPayments.Any(payment => payment.Status == PaymentStatus.Cancelled));
            }
        }
    }

    private static decimal ResolveEffectiveInvoiceOutstandingAmount(
        decimal invoiceAmount,
        decimal paidAmount,
        PaymentStatus status,
        decimal sourceOutstandingAmount,
        bool hasCancelledRelatedPayments)
    {
        if (status is PaymentStatus.Completed or PaymentStatus.Cancelled)
        {
            return 0m;
        }

        if (hasCancelledRelatedPayments && sourceOutstandingAmount <= 0m)
        {
            return 0m;
        }

        if (sourceOutstandingAmount > 0m)
        {
            return NormalizeMoneyAmount(sourceOutstandingAmount);
        }

        return NormalizeMoneyAmount(Math.Max(
            NormalizeMoneyAmount(invoiceAmount) - NormalizeMoneyAmount(paidAmount),
            0m));
    }

    private async Task<List<InvoiceLineItemDto>> LoadInvoiceLineItemsAsync(int paymentId, string? systemNotes, CancellationToken ct)
    {
        var stored = await _db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == paymentId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new InvoiceLineItemDto
            {
                Id = item.Id,
                SortOrder = item.SortOrder,
                SourceKey = InvoiceLineItemStorage.BuildSourceKey(item.Id, item.SortOrder),
                ServiceId = item.ServiceId,
                Name = item.Name,
                Quantity = item.Quantity,
                Unit = item.Unit,
                Price = item.Price,
            })
            .ToListAsync(ct);

        return stored.Count > 0
            ? stored
            : InvoiceLineItemStorage.ExtractFromSystemNotes(systemNotes);
    }

    private async Task<Dictionary<int, List<InvoiceLineItemDto>>> LoadLineItemsMapAsync(IEnumerable<int> paymentIds, CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceLineItemDto>>();
        }

        var rows = await _db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => ids.Contains(item.PaymentId))
            .OrderBy(item => item.PaymentId)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.PaymentId,
                LineItem = new InvoiceLineItemDto
                {
                    Id = item.Id,
                    SortOrder = item.SortOrder,
                    SourceKey = InvoiceLineItemStorage.BuildSourceKey(item.Id, item.SortOrder),
                    ServiceId = item.ServiceId,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    Price = item.Price,
                },
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.PaymentId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.LineItem).ToList());
    }

    private async Task<ActionResult?> SyncInstallmentPaymentsAsync(
        Payment invoicePayment,
        UpsertInvoiceRequest request,
        IReadOnlyCollection<int> responsibleIds,
        CancellationToken ct)
    {
        var invoiceEntityId = invoicePayment.InvoiceId ?? invoicePayment.Invoice?.Id;
        if (!invoiceEntityId.HasValue || invoiceEntityId.Value <= 0)
        {
            return null;
        }
        var invoiceTimeZone = await ResolveInvoiceTimeZoneAsync(ct);

        var existingPaymentRows = await _db.Payments
            .Include(payment => payment.Responsibles)
            .Include(payment => payment.InvoiceClientCases)
            .Where(payment =>
                payment.InvoiceId == invoiceEntityId.Value
                && payment.Id != invoicePayment.Id)
            .ToListAsync(ct);

        var existingInstallmentPayments = existingPaymentRows
            .Select(payment => new
            {
                Payment = payment,
                Metadata = ExtractInstallmentPaymentMetadata(payment.SystemNotes),
            })
            .Where(item => item.Metadata is not null)
            .ToList();
        var legacyInstallmentCandidates = existingPaymentRows
            .Where(IsLegacyInvoiceInstallmentCandidate)
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .ToList();

        if (!ShouldReplaceInstallmentPlan(request))
        {
            SyncGeneratedInstallmentBindings(invoicePayment, existingInstallmentPayments.Select(item => item.Payment).ToList(), responsibleIds);
            return null;
        }

        if (request.InstallmentPlan is null || request.InstallmentPlan.Items.Count == 0)
        {
            var paymentsToRemove = existingInstallmentPayments
                .Select(item => item.Payment)
                .Concat(legacyInstallmentCandidates)
                .DistinctBy(payment => payment.Id)
                .ToList();

            foreach (var existing in paymentsToRemove)
            {
                if (HasProtectedInstallmentActivity(existing))
                {
                    return BadRequest("Нельзя удалить график рассрочки: по одному из платежей уже есть движение.");
                }
            }

            if (paymentsToRemove.Count > 0)
            {
                _db.Payments.RemoveRange(paymentsToRemove);
            }

            return null;
        }

        var normalizedItems = request.InstallmentPlan.Items
            .Where(item => item is not null)
            .OrderBy(item => item.Sequence)
            .ToList();

        if (normalizedItems.Count == 0)
        {
            return BadRequest("График рассрочки пуст.");
        }

        if (normalizedItems.Any(item =>
                item.Sequence <= 0
                || item.Payment < 0
                || item.Principal < 0
                || item.Interest < 0
                || item.Balance < 0))
        {
            return BadRequest("График рассрочки содержит некорректные значения.");
        }

        var duplicateSequence = normalizedItems
            .GroupBy(item => item.Sequence)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            return BadRequest($"График рассрочки содержит дублирующийся номер платежа: {duplicateSequence.Key}.");
        }

        var existingBySequence = existingInstallmentPayments
            .GroupBy(item => item.Metadata!.Sequence)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Payment.Id).ToList());

        var retainedPaymentIds = new HashSet<int>();
        foreach (var item in normalizedItems)
        {
            existingBySequence.TryGetValue(item.Sequence, out var matches);
            var existing = matches?.FirstOrDefault();

            if (existing is not null)
            {
                retainedPaymentIds.Add(existing.Payment.Id);

                if (HasProtectedInstallmentActivity(existing.Payment)
                    && !InstallmentPaymentMatches(existing.Payment, item, request, invoicePayment))
                {
                    return BadRequest($"Нельзя изменить платеж рассрочки №{item.Sequence}: по нему уже есть движение.");
                }

                ApplyInstallmentPayment(
                    existing.Payment,
                    invoicePayment,
                    request,
                    request.InstallmentPlan,
                    item,
                    responsibleIds,
                    invoiceTimeZone);
                continue;
            }

            var legacyInstallment = FindLegacyInstallmentCandidate(legacyInstallmentCandidates, item, retainedPaymentIds);
            if (legacyInstallment is not null)
            {
                retainedPaymentIds.Add(legacyInstallment.Id);

                if (HasProtectedInstallmentActivity(legacyInstallment)
                    && !InstallmentPaymentMatches(legacyInstallment, item, request, invoicePayment))
                {
                    return BadRequest(BuildLegacyInstallmentConflictMessage(legacyInstallment));
                }

                ApplyInstallmentPayment(
                    legacyInstallment,
                    invoicePayment,
                    request,
                    request.InstallmentPlan,
                    item,
                    responsibleIds,
                    invoiceTimeZone);
                continue;
            }

            var generatedPayment = new Payment
            {
                CreatedAt = DateTime.UtcNow,
            };

            ApplyInstallmentPayment(
                generatedPayment,
                invoicePayment,
                request,
                request.InstallmentPlan,
                item,
                responsibleIds,
                invoiceTimeZone);

            _db.Payments.Add(generatedPayment);
        }

        foreach (var existing in existingInstallmentPayments)
        {
            if (retainedPaymentIds.Contains(existing.Payment.Id))
            {
                continue;
            }

            if (HasProtectedInstallmentActivity(existing.Payment))
            {
                return BadRequest($"Нельзя удалить платеж рассрочки №{existing.Metadata!.Sequence}: по нему уже есть движение.");
            }

            _db.Payments.Remove(existing.Payment);
        }

        foreach (var legacyPayment in legacyInstallmentCandidates.Where(payment => !retainedPaymentIds.Contains(payment.Id)))
        {
            if (HasProtectedInstallmentActivity(legacyPayment))
            {
                return BadRequest(BuildLegacyInstallmentConflictMessage(legacyPayment));
            }

            _db.Payments.Remove(legacyPayment);
        }

        return null;
    }

    private static bool IsLegacyInvoiceInstallmentCandidate(Payment payment)
    {
        return payment.DocumentKind == DocumentKind.Invoice
            && ExtractInstallmentPaymentMetadata(payment.SystemNotes) is null;
    }

    private static Payment? FindLegacyInstallmentCandidate(
        IReadOnlyCollection<Payment> candidates,
        InvoiceInstallmentPlanItemRequest item,
        IReadOnlySet<int> retainedPaymentIds)
    {
        var availableCandidates = candidates
            .Where(payment => !retainedPaymentIds.Contains(payment.Id))
            .ToList();

        return availableCandidates
            .Where(payment => LegacyInstallmentDateMatches(payment, item) && Math.Abs(payment.Amount - item.Payment) <= 0.01m)
            .OrderBy(payment => payment.Id)
            .FirstOrDefault()
            ?? availableCandidates
                .Where(payment => LegacyInstallmentDateMatches(payment, item) && !HasProtectedInstallmentActivity(payment))
                .OrderBy(payment => payment.Id)
                .FirstOrDefault()
            ?? availableCandidates
                .Where(payment => !HasProtectedInstallmentActivity(payment))
                .OrderBy(payment => payment.Date)
                .ThenBy(payment => payment.Id)
                .FirstOrDefault();
    }

    private static bool LegacyInstallmentDateMatches(Payment payment, InvoiceInstallmentPlanItemRequest item)
    {
        return payment.Date.Date == item.Date.Date
            || payment.PlannedDate.Date == item.Date.Date;
    }

    private static string BuildLegacyInstallmentConflictMessage(Payment payment)
    {
        return "Нельзя перестроить график рассрочки: "
            + $"старый платеж от {payment.Date:dd.MM.yyyy} на сумму {payment.Amount:0.##} уже имеет оплату. "
            + "Удалите или отредактируйте этот платеж вручную.";
    }

    private async Task SyncLinkedPaymentBindingsAsync(
        Payment invoicePayment,
        IReadOnlyCollection<int> clientCaseIds,
        IReadOnlyCollection<int> responsibleIds,
        int? preservedProtectedPaymentId,
        CancellationToken ct)
    {
        var invoiceEntityId = invoicePayment.InvoiceId ?? invoicePayment.Invoice?.Id;
        if (!invoiceEntityId.HasValue || invoiceEntityId.Value <= 0)
        {
            return;
        }

        var siblingPayments = await _db.Payments
            .Include(payment => payment.Responsibles)
            .Where(payment =>
                payment.InvoiceId == invoiceEntityId.Value
                && payment.Id != invoicePayment.Id
                && (!preservedProtectedPaymentId.HasValue || payment.Id != preservedProtectedPaymentId.Value)
                && payment.DocumentKind != DocumentKind.Invoice)
            .ToListAsync(ct);
        var hasGeneratedInstallments = siblingPayments.Any(InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment);
        var linkedPayments = siblingPayments
            .Where(payment => !InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment))
            .ToList();

        foreach (var linkedPayment in linkedPayments)
        {
            SyncPaymentBindingsFromInvoice(linkedPayment, invoicePayment);
            await SyncInvoiceClientCasesAsync(linkedPayment, clientCaseIds, ct);
            SyncPaymentResponsibles(linkedPayment, responsibleIds);
        }

        if (!hasGeneratedInstallments && linkedPayments.Count == 1)
        {
            SyncSingleLinkedPaymentDueDate(linkedPayments[0], invoicePayment);
        }

        foreach (var linkedPayment in linkedPayments)
        {
            PaymentDomainService.Normalize(linkedPayment);
        }
    }

    private static void SyncSingleLinkedPaymentDueDate(Payment linkedPayment, Payment invoicePayment)
    {
        var dueDate = PaymentDomainService.NormalizeLocalDate(invoicePayment.Date);
        linkedPayment.Date = dueDate;
        linkedPayment.PlannedDate = dueDate;
    }

    private static void SyncPaymentBindingsFromInvoice(Payment payment, Payment invoicePayment)
    {
        payment.Account = invoicePayment.Account;
        payment.AccountDate = invoicePayment.AccountDate;
        payment.Type = invoicePayment.Type;
        payment.ClientId = invoicePayment.ClientId;
        payment.LegalEntityId = invoicePayment.LegalEntityId;
        payment.ExpenseClientId = invoicePayment.ExpenseClientId;
        payment.ExpenseLegalEntityId = invoicePayment.ExpenseLegalEntityId;
        payment.ContractId = invoicePayment.ContractId;
        payment.ClientCaseId = invoicePayment.ClientCaseId;
        payment.ClientCaseReference = invoicePayment.ClientCaseReference;
        payment.PaymentSourceId = invoicePayment.PaymentSourceId;
        payment.ServiceId = invoicePayment.ServiceId;
        payment.IncomeTypeId = invoicePayment.IncomeTypeId;
        payment.DealTypeId = invoicePayment.DealTypeId;
        payment.PaymentStatusId = invoicePayment.PaymentStatusId;
    }

    private void SyncGeneratedInstallmentBindings(
        Payment invoicePayment,
        IReadOnlyCollection<Payment> generatedInstallments,
        IReadOnlyCollection<int> responsibleIds)
    {
        foreach (var installment in generatedInstallments)
        {
            SyncPaymentBindingsFromInvoice(installment, invoicePayment);
            SyncInvoiceClientCasesFromInvoice(installment, invoicePayment);
            SyncPaymentResponsibles(installment, responsibleIds);
        }
    }

    private Task<List<Payment>> LoadGeneratedInstallmentPaymentsAsync(
        int invoiceEntityId,
        int representativePaymentId,
        CancellationToken ct)
        => _db.Payments
            .Include(payment => payment.PaymentSource)
            .Where(payment =>
                payment.InvoiceId == invoiceEntityId
                && payment.Id != representativePaymentId
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains(InstallmentPaymentMarker))
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .ToListAsync(ct);

    private ActionResult? CancelUnpaidGeneratedInstallmentPayments(
        Payment invoicePayment,
        IReadOnlyCollection<Payment> generatedPayments,
        string reason,
        DateTime now,
        TimeZoneInfo invoiceTimeZone)
    {
        if (generatedPayments.Count == 0)
        {
            return BadRequest("У счёта нет сформированного графика платежей.");
        }

        var actor = User.GetDisplayName();
        var activeGeneratedPayments = generatedPayments
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .ToList();
        var paidGeneratedPayments = activeGeneratedPayments
            .Where(HasReceivedMoney)
            .ToList();
        if (paidGeneratedPayments.Count == 0)
        {
            return BadRequest("Этот режим доступен для частично оплаченной рассрочки: нужен хотя бы один оплаченный платёж, который останется в истории.");
        }

        var unpaidGeneratedPayments = activeGeneratedPayments
            .Where(payment => !HasReceivedMoney(payment))
            .ToList();
        if (unpaidGeneratedPayments.Count == 0)
        {
            return BadRequest("Не найдено неоплаченных платежей графика для отмены.");
        }

        foreach (var payment in unpaidGeneratedPayments)
        {
            payment.Status = PaymentStatus.Cancelled;
            payment.PaidAmount = 0m;
            payment.IsPaid = false;
            payment.PaidDate = null;
            payment.LastPaymentDate = null;
            PaymentDomainService.AppendSystemNote(payment, $"Неоплаченный платёж рассрочки отменён. Причина: {reason}", now, actor, invoiceTimeZone);
            PaymentDomainService.Normalize(payment);
        }

        RefreshInvoiceRepresentativeFromGeneratedPayments(invoicePayment, generatedPayments, now);
        PaymentDomainService.AppendSystemNote(
            invoicePayment,
            $"Отменены только неоплаченные платежи графика рассрочки ({unpaidGeneratedPayments.Count}). Оплаченные платежи сохранены: {paidGeneratedPayments.Count}. Причина: {reason}",
            now,
            actor,
            invoiceTimeZone);
        return null;
    }

    private static void RefreshInvoiceRepresentativeFromGeneratedPayments(
        Payment representative,
        IReadOnlyCollection<Payment> generatedPayments,
        DateTime now)
    {
        if (generatedPayments.Count == 0)
        {
            return;
        }

        InvoicePaymentStateService.ApplySnapshot(
            representative,
            InvoicePaymentStateService.CalculateRepresentativeState(representative, generatedPayments, now));
    }

    private ActionResult? CancelGeneratedInstallmentPayments(
        IReadOnlyCollection<Payment> generatedPayments,
        string reason,
        bool allowReceivedMoney,
        DateTime now,
        TimeZoneInfo invoiceTimeZone)
    {
        var actor = User.GetDisplayName();
        foreach (var payment in generatedPayments)
        {
            if (payment.Status == PaymentStatus.Cancelled)
            {
                continue;
            }

            if (!allowReceivedMoney && HasReceivedMoney(payment))
            {
                return BadRequest("Нельзя аннулировать счёт: по одному из платежей рассрочки уже есть оплата.");
            }

            payment.Status = PaymentStatus.Cancelled;
            payment.PaidAmount = 0m;
            payment.IsPaid = false;
            payment.PaidDate = null;
            payment.LastPaymentDate = null;
            PaymentDomainService.AppendSystemNote(payment, $"Платёж рассрочки аннулирован вместе со счётом. Причина: {reason}", now, actor, invoiceTimeZone);
        }

        return null;
    }

    private async Task<MoneyOperation> CreateInvoiceRefundOperationAsync(
        Payment payment,
        IReadOnlyCollection<Payment> paymentsToCancel,
        int invoiceEntityId,
        decimal amount,
        string reason,
        CancelInvoiceRequest request,
        DateTime now,
        CancellationToken ct)
    {
        var sourcePayment = paymentsToCancel.FirstOrDefault(HasReceivedMoney) ?? payment;
        var refundDate = (request.RefundDate ?? now).Date;
        var refundSource = request.RefundSource ?? ResolveMoneyOperationSource(sourcePayment.PaymentSource?.Name);
        var counterpartyName = payment.Client?.Name
            ?? payment.LegalEntity?.ShortName
            ?? payment.LegalEntity?.FullName
            ?? payment.CounterpartyName;
        var invoiceNumber = string.IsNullOrWhiteSpace(payment.Account) ? payment.Invoice?.Number : payment.Account;
        var purpose = $"Возврат оплаты по аннулированному счёту №{invoiceNumber}. Причина: {reason}";

        var operation = new MoneyOperation
        {
            Source = refundSource,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = sourcePayment.PaymentSource?.Name,
            CounterpartyName = counterpartyName,
            CategoryName = "Возврат клиенту",
            Amount = amount,
            Currency = "RUB",
            Date = refundDate,
            RecognitionDate = refundDate,
            Purpose = purpose,
            PaidInvoiceNumber = invoiceNumber,
            PaidAmount = amount,
            PaidAccountName = invoiceNumber,
            LinkedInvoiceId = invoiceEntityId,
            MatchStatus = MoneyOperationMatchStatus.Matched,
            MatchedAt = now,
            MatchComment = "Возврат оплаты при аннулировании счёта.",
            CreatedAt = now,
        };

        _db.MoneyOperations.Add(operation);
        await _db.SaveChangesAsync(ct);

        if (refundSource == MoneyOperationSource.Cash)
        {
            _db.CashOrders.Add(new CashOrder
            {
                Number = await GenerateCashOrderNumberAsync(CashOrderType.Outgoing, refundDate, ct),
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = refundDate,
                Amount = amount,
                Currency = "RUB",
                ClientId = payment.ClientId,
                MoneyOperationId = operation.Id,
                InvoiceId = invoiceEntityId,
                Account = sourcePayment.PaymentSource?.Name,
                ReceiverName = counterpartyName,
                Basis = purpose,
                Comment = reason,
                CreatedAt = now,
            });
            await _db.SaveChangesAsync(ct);
        }

        return operation;
    }

    private static MoneyOperationSource ResolveMoneyOperationSource(string? sourceName)
    {
        if (ContainsPaymentSourceToken(sourceName, "кас") ||
            ContainsPaymentSourceToken(sourceName, "налич"))
        {
            return MoneyOperationSource.Cash;
        }

        if (ContainsPaymentSourceToken(sourceName, "карт") ||
            ContainsPaymentSourceToken(sourceName, "card"))
        {
            return MoneyOperationSource.Card;
        }

        if (ContainsPaymentSourceToken(sourceName, "e-money") ||
            ContainsPaymentSourceToken(sourceName, "emoney") ||
            ContainsPaymentSourceToken(sourceName, "электрон") ||
            ContainsPaymentSourceToken(sourceName, "yoomoney") ||
            ContainsPaymentSourceToken(sourceName, "яндекс"))
        {
            return MoneyOperationSource.EMoney;
        }

        return MoneyOperationSource.Bank;
    }

    private static bool ContainsPaymentSourceToken(string? sourceName, string token)
        => sourceName?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct)
    {
        var prefix = type == CashOrderType.Incoming ? "ПКО" : "РКО";
        var yearPrefix = $"{prefix}-{date:yyyy}-";
        var count = await _db.CashOrders.CountAsync(order => order.Number.StartsWith(yearPrefix), ct);
        return $"{yearPrefix}{count + 1:0000}";
    }

    private static bool HasReceivedMoney(Payment payment)
        => payment.PaidAmount > 0m
           || payment.IsPaid
           || payment.PaidDate.HasValue
           || payment.LastPaymentDate.HasValue
           || payment.Status == PaymentStatus.Completed;

    private static bool HasSingleCounterpartyOwner(int? clientId, int? legalEntityId)
        => (clientId.HasValue && clientId.Value > 0 && !legalEntityId.HasValue)
            || (legalEntityId.HasValue && legalEntityId.Value > 0 && !clientId.HasValue);

    private static bool ShouldReplaceInstallmentPlan(UpsertInvoiceRequest request)
    {
        return request.ReplaceInstallmentPlan || request.InstallmentPlan is not null;
    }

    private async Task LockInvoiceAggregateAsync(int invoiceEntityId, CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql())
        {
            return;
        }

        await _db.Database
            .SqlQuery<int>($"SELECT 1 FROM \"Payments\" WHERE \"InvoiceId\" = {invoiceEntityId} FOR UPDATE")
            .ToListAsync(ct);
    }

    private static DateTime ResolveInvoiceDueDate(UpsertInvoiceRequest request)
    {
        var installmentDueDate = request.InstallmentPlan?.Items?
            .Where(item => item is not null)
            .Select(item => (DateTime?)PaymentDomainService.NormalizeLocalDate(item.Date))
            .Max();

        if (installmentDueDate.HasValue && installmentDueDate.Value != default)
        {
            return installmentDueDate.Value;
        }

        return PaymentDomainService.NormalizeLocalDate(request.DueDate ?? request.Date);
    }

    private static DateTime ResolveInvoiceDueDate(
        UpsertInvoiceRequest request,
        Payment existingPayment,
        DateTime nextInvoiceDate)
    {
        if (request.InstallmentPlan is not null)
        {
            return ResolveInvoiceDueDate(request);
        }

        var requestedDueDate = request.DueDate.HasValue
            ? PaymentDomainService.NormalizeLocalDate(request.DueDate.Value)
            : (DateTime?)null;
        if (!requestedDueDate.HasValue)
        {
            return PaymentDomainService.NormalizeLocalDate(nextInvoiceDate);
        }

        var previousDueDate = PaymentDomainService.NormalizeLocalDate(existingPayment.Date);
        var previousInvoiceDate = PaymentDomainService.NormalizeLocalDate(
            existingPayment.Invoice?.Date
            ?? existingPayment.AccountDate
            ?? existingPayment.Date);

        if (requestedDueDate.Value.Date == previousDueDate.Date
            && previousDueDate.Date == previousInvoiceDate.Date
            && nextInvoiceDate.Date != previousInvoiceDate.Date)
        {
            return PaymentDomainService.NormalizeLocalDate(nextInvoiceDate);
        }

        return requestedDueDate.Value;
    }

    private void ApplyInstallmentPayment(
        Payment payment,
        Payment invoicePayment,
        UpsertInvoiceRequest request,
        InvoiceInstallmentPlanRequest plan,
        InvoiceInstallmentPlanItemRequest item,
        IReadOnlyCollection<int> responsibleIds,
        TimeZoneInfo invoiceTimeZone)
    {
        var invoiceEntityId = invoicePayment.InvoiceId ?? invoicePayment.Invoice?.Id;
        payment.InvoiceId = invoiceEntityId;
        payment.Account = invoicePayment.Account;
        payment.AccountDate = invoicePayment.AccountDate;
        payment.DocumentKind = DocumentKind.Payment;
        var installmentDate = PaymentDomainService.NormalizeLocalDate(item.Date);
        payment.Date = installmentDate;
        payment.PlannedDate = installmentDate;
        payment.Amount = Math.Max(item.Payment, 0);
        payment.Type = invoicePayment.Type;
        payment.Status = PaymentStatus.Pending;
        payment.Description = BuildInstallmentDescription(invoicePayment.Account, item.Sequence, plan.Items.Count);
        payment.Notes = BuildInstallmentNotes(item);
        payment.ClientId = request.ClientId;
        payment.LegalEntityId = request.LegalEntityId;
        payment.ExpenseClientId = invoicePayment.ExpenseClientId;
        payment.ExpenseLegalEntityId = invoicePayment.ExpenseLegalEntityId;
        payment.ContractId = request.ContractId;
        payment.ClientCaseId = invoicePayment.ClientCaseId;
        payment.ClientCaseReference = invoicePayment.ClientCaseReference;
        payment.PaymentSourceId = request.PaymentSourceId;
        payment.ServiceId = request.ServiceId;
        payment.IncomeTypeId = request.IncomeTypeId;
        payment.DealTypeId = request.DealTypeId;
        payment.PaymentStatusId = request.PaymentStatusEntityId;
        payment.CounterpartyName = null;
        payment.SystemNotes = MergeInstallmentMetadataIntoSystemNotes(payment.SystemNotes, plan, item);
        SyncInvoiceClientCasesFromInvoice(payment, invoicePayment);

        if (!HasProtectedInstallmentActivity(payment))
        {
            payment.PaidAmount = 0m;
            payment.PaidDate = null;
            payment.LastPaymentDate = null;
            payment.IsPaid = false;
        }

        SyncPaymentResponsibles(payment, responsibleIds);
        PaymentDomainService.Normalize(payment);
        PaymentDomainService.ApplyStatusRules(payment, DateTime.UtcNow, invoiceTimeZone);
    }

    private static void SyncInvoiceClientCasesFromInvoice(Payment payment, Payment invoicePayment)
    {
        var targetIds = invoicePayment.InvoiceClientCases
            .Select(link => link.ClientCaseId)
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();

        var toRemove = payment.InvoiceClientCases
            .Where(link => !targetIds.Contains(link.ClientCaseId))
            .ToList();
        foreach (var link in toRemove)
        {
            payment.InvoiceClientCases.Remove(link);
        }

        var existingIds = payment.InvoiceClientCases.Select(link => link.ClientCaseId).ToHashSet();
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

        payment.ClientCaseId = targetIds.Count > 0 ? targetIds.First() : null;
    }

    private static void SyncPaymentResponsibles(Payment payment, IReadOnlyCollection<int> responsibleIds)
    {
        var toRemove = payment.Responsibles
            .Where(item => !responsibleIds.Contains(item.UserId))
            .ToList();

        foreach (var row in toRemove)
        {
            payment.Responsibles.Remove(row);
        }

        var existingIds = payment.Responsibles.Select(item => item.UserId).ToHashSet();
        foreach (var responsibleId in responsibleIds)
        {
            if (existingIds.Contains(responsibleId))
            {
                continue;
            }

            payment.Responsibles.Add(new PaymentResponsible
            {
                PaymentId = payment.Id,
                UserId = responsibleId,
            });
        }
    }

    private static bool HasProtectedInstallmentActivity(Payment payment)
    {
        if (payment.PaidAmount > 0m || payment.IsPaid || payment.PaidDate.HasValue || payment.LastPaymentDate.HasValue)
        {
            return true;
        }

        return payment.Status is PaymentStatus.Completed or PaymentStatus.Processing or PaymentStatus.Cancelled;
    }

    private static bool InstallmentPaymentMatches(
        Payment payment,
        InvoiceInstallmentPlanItemRequest item,
        UpsertInvoiceRequest request,
        Payment invoicePayment)
    {
        var sameDate = payment.Date.Date == item.Date.Date;
        var sameAmount = Math.Abs(payment.Amount - item.Payment) <= 0.01m;
        var sameClient = payment.ClientId == request.ClientId;
        var sameLegalEntity = payment.LegalEntityId == request.LegalEntityId;
        var sameExpenseClient = payment.ExpenseClientId == invoicePayment.ExpenseClientId;
        var sameExpenseLegalEntity = payment.ExpenseLegalEntityId == invoicePayment.ExpenseLegalEntityId;
        var sameCase = payment.ClientCaseId == invoicePayment.ClientCaseId;
        var sameContract = payment.ContractId == request.ContractId;
        return sameDate && sameAmount && sameClient && sameLegalEntity && sameExpenseClient && sameExpenseLegalEntity && sameCase && sameContract;
    }

    private static string BuildInstallmentDescription(string? invoiceNumber, int sequence, int totalCount)
    {
        if (!string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return $"Рассрочка по счету №{invoiceNumber} · платеж {sequence}/{Math.Max(totalCount, sequence)}";
        }

        return $"Платеж по рассрочке {sequence}/{Math.Max(totalCount, sequence)}";
    }

    private static string BuildInstallmentNotes(InvoiceInstallmentPlanItemRequest item)
    {
        static string FormatAmount(decimal amount) => $"{amount.ToString("N2", RuCulture)} ₽";

        return string.Join(
            "\n",
            new[]
            {
                $"Основной долг: {FormatAmount(item.Principal)}",
                $"Проценты: {FormatAmount(item.Interest)}",
                $"Остаток: {FormatAmount(item.Balance)}",
            });
    }

    private static string MergeInstallmentMetadataIntoSystemNotes(
        string? existingSystemNotes,
        InvoiceInstallmentPlanRequest plan,
        InvoiceInstallmentPlanItemRequest item)
    {
        var baseLines = (existingSystemNotes ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(InstallmentPaymentMarker, StringComparison.Ordinal))
            .ToList();

        var payload = new InstallmentPaymentMetadataDto
        {
            Sequence = item.Sequence,
            Total = Math.Max(plan.Total, 0),
            DownPayment = Math.Max(plan.DownPayment, 0),
            AnnualRate = Math.Max(plan.AnnualRate, 0),
            Months = Math.Max(plan.Months, 1),
            StartDate = plan.StartDate.Date,
            RoundingMode = plan.RoundingMode,
            RoundingStep = plan.RoundingStep,
            ToPay = Math.Max(plan.ToPay, 0),
            Overpay = Math.Max(plan.Overpay, 0),
            LoanAmount = plan.LoanAmount,
            BaseMonthlyPayment = plan.BaseMonthlyPayment,
            RoundedMonthlyPayment = plan.RoundedMonthlyPayment,
            Item = new InstallmentPaymentMetadataItemDto
            {
                Sequence = item.Sequence,
                Date = item.Date.Date,
                Principal = Math.Max(item.Principal, 0),
                Interest = Math.Max(item.Interest, 0),
                Payment = Math.Max(item.Payment, 0),
                Balance = Math.Max(item.Balance, 0),
            },
        };

        baseLines.Insert(0, $"{InstallmentPaymentMarker} {JsonSerializer.Serialize(payload)}");
        return string.Join("\n", baseLines);
    }

    private static InstallmentPaymentMetadataDto? ExtractInstallmentPaymentMetadata(string? systemNotes)
    {
        if (string.IsNullOrWhiteSpace(systemNotes))
        {
            return null;
        }

        var markerLine = systemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(InstallmentPaymentMarker, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(markerLine))
        {
            return null;
        }

        var json = markerLine[InstallmentPaymentMarker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InstallmentPaymentMetadataDto>(json, InstallmentMetadataJsonOptions);
        }
        catch
        {
            return null;
        }
    }


    private async Task<Dictionary<int, List<InvoiceResponsibleDto>>> LoadResponsiblesMapAsync(IEnumerable<int> paymentIds, CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceResponsibleDto>>();
        }

        var rows = await _db.PaymentResponsibles
            .AsNoTracking()
            .Include(pr => pr.User)
            .Where(pr => ids.Contains(pr.PaymentId))
            .OrderBy(pr => pr.User!.LastName)
            .ThenBy(pr => pr.User!.FirstName)
            .ThenBy(pr => pr.User!.MiddleName)
            .ThenBy(pr => pr.User!.FullName)
            .Select(pr => new
            {
                pr.PaymentId,
                pr.UserId,
                FullName = string.IsNullOrWhiteSpace(pr.User!.LastName)
                    ? (string.IsNullOrWhiteSpace(pr.User.FullName) ? pr.User.Email : pr.User.FullName)
                    : ($"{pr.User.LastName} {((string.IsNullOrWhiteSpace(pr.User.FirstName) ? string.Empty : pr.User.FirstName[0] + "."))}{((string.IsNullOrWhiteSpace(pr.User.MiddleName) ? string.Empty : pr.User.MiddleName[0] + "."))}").Trim(),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.PaymentId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new InvoiceResponsibleDto
                {
                    Id = x.UserId,
                    FullName = x.FullName,
                }).ToList());
    }

    private async Task<Dictionary<int, List<InvoiceActDto>>> LoadActsMapAsync(IEnumerable<int> paymentIds, CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceActDto>>();
        }

        var invoiceMeta = await _db.Payments
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                Number = x.Invoice != null ? x.Invoice.Number : x.Account,
            })
            .ToListAsync(ct);

        var invoiceIdsByNumber = invoiceMeta
            .Where(x => !string.IsNullOrWhiteSpace(x.Number))
            .GroupBy(x => x.Number!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Id).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var invoiceNumbers = invoiceIdsByNumber.Keys.ToList();

        var rows = await _db.Acts
            .AsNoTracking()
            .Where(a => (a.InvoiceId.HasValue && ids.Contains(a.InvoiceId.Value))
                || (!a.InvoiceId.HasValue && a.InvoiceNumber != null && invoiceNumbers.Contains(a.InvoiceNumber)))
            .OrderByDescending(a => a.Date)
            .ThenByDescending(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.InvoiceId,
                a.InvoiceNumber,
                Act = new InvoiceActDto
                {
                    Id = a.Id,
                    Number = a.Number,
                    Title = a.Title,
                    Date = a.Date,
                    Amount = a.Amount,
                    CreationMode = a.CreationMode,
                    Status = a.Status,
                    WorkflowStatus = a.WorkflowStatus,
                    ClientId = a.ClientId,
                    ClientName = a.Client != null ? a.Client.Name : null,
                    ResponsibleId = a.ResponsibleId,
                    ResponsibleName = a.Responsible != null
                        ? (string.IsNullOrWhiteSpace(a.Responsible.FullName) ? a.Responsible.Email : a.Responsible.FullName)
                        : null,
                    Comment = a.Comment,
                    LineItems = a.LineItems
                        .OrderBy(item => item.SortOrder)
                        .ThenBy(item => item.Id)
                        .Select(item => new ActLineItemDto
                        {
                            Id = item.Id,
                            InvoiceLineItemId = item.InvoiceLineItemId,
                            SourceKey = item.SourceKey,
                            SourceSortOrder = item.SourceSortOrder,
                            Name = item.Name,
                            Quantity = item.Quantity,
                            Unit = item.Unit,
                            Price = item.Price,
                        })
                        .ToList(),
                },
            })
            .ToListAsync(ct);

        var result = new Dictionary<int, List<InvoiceActDto>>();
        foreach (var row in rows)
        {
            var targetIds = new List<int>();
            if (row.InvoiceId.HasValue && ids.Contains(row.InvoiceId.Value))
            {
                targetIds.Add(row.InvoiceId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(row.InvoiceNumber)
                && invoiceIdsByNumber.TryGetValue(row.InvoiceNumber.Trim(), out var matchedInvoiceIds))
            {
                targetIds.AddRange(matchedInvoiceIds);
            }

            foreach (var targetId in targetIds.Distinct())
            {
                if (!result.TryGetValue(targetId, out var bucket))
                {
                    bucket = new List<InvoiceActDto>();
                    result[targetId] = bucket;
                }

                if (bucket.Any(x => x.Id == row.Act.Id))
                {
                    continue;
                }

                bucket.Add(row.Act);
            }
        }

        return result;
    }

    private async Task<Dictionary<int, List<InvoiceClientCaseDto>>> LoadClientCasesMapAsync(IEnumerable<int> paymentIds, CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceClientCaseDto>>();
        }

        var paymentMeta = await _db.Payments
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.ClientCaseId,
            })
            .ToListAsync(ct);

        var legacyCaseIds = paymentMeta
            .Where(x => x.ClientCaseId.HasValue)
            .Select(x => x.ClientCaseId!.Value)
            .Distinct()
            .ToList();

        var legacyCases = legacyCaseIds.Count == 0
            ? new Dictionary<int, InvoiceClientCaseDto>()
            : await _db.ClientCases
                .AsNoTracking()
                .Where(x => legacyCaseIds.Contains(x.Id))
                .Select(x => new InvoiceClientCaseDto
                {
                    Id = x.Id,
                    ClientId = x.ClientId,
                    LegalEntityId = x.LegalEntityId,
                    Title = x.Title,
                    Status = x.Status,
                    CreatedAt = x.CreatedAt,
                })
                .ToDictionaryAsync(x => x.Id, ct);

        var result = new Dictionary<int, List<InvoiceClientCaseDto>>();

        foreach (var payment in paymentMeta)
        {
            if (!payment.ClientCaseId.HasValue || !legacyCases.TryGetValue(payment.ClientCaseId.Value, out var clientCase))
            {
                continue;
            }

            result[payment.Id] = new List<InvoiceClientCaseDto> { clientCase };
        }

        var linkedRows = await _db.InvoiceClientCases
            .AsNoTracking()
            .Where(x => ids.Contains(x.PaymentId))
            .OrderByDescending(x => x.ClientCase.CreatedAt)
            .ThenByDescending(x => x.ClientCaseId)
            .Select(x => new
            {
                x.PaymentId,
                ClientCase = new InvoiceClientCaseDto
                {
                    Id = x.ClientCase.Id,
                    ClientId = x.ClientCase.ClientId,
                    LegalEntityId = x.ClientCase.LegalEntityId,
                    Title = x.ClientCase.Title,
                    Status = x.ClientCase.Status,
                    CreatedAt = x.ClientCase.CreatedAt,
                },
            })
            .ToListAsync(ct);

        foreach (var row in linkedRows)
        {
            if (!result.TryGetValue(row.PaymentId, out var bucket))
            {
                bucket = new List<InvoiceClientCaseDto>();
                result[row.PaymentId] = bucket;
            }

            if (bucket.Any(x => x.Id == row.ClientCase.Id))
            {
                continue;
            }

            bucket.Add(row.ClientCase);
        }

        return result;
    }

    private async Task<BadRequestObjectResult?> ValidateInvoiceLineItemServicesAsync(IEnumerable<InvoiceLineItemRequest>? lineItems, CancellationToken ct)
    {
        if (lineItems is null)
        {
            return null;
        }

        var requestedServiceIds = lineItems
            .Where(item => item?.ServiceId is > 0)
            .Select(item => item!.ServiceId!.Value)
            .Distinct()
            .ToList();

        if (requestedServiceIds.Count == 0)
        {
            return null;
        }

        var existingServiceIds = await _db.Services
            .AsNoTracking()
            .Where(service => requestedServiceIds.Contains(service.Id))
            .Select(service => service.Id)
            .ToListAsync(ct);

        var unknownServiceId = requestedServiceIds.Except(existingServiceIds).OrderBy(id => id).FirstOrDefault();
        return unknownServiceId > 0
            ? BadRequest($"Unknown invoice line item serviceId {unknownServiceId}")
            : null;
    }

    private async Task SyncInvoiceClientCasesAsync(Payment payment, IReadOnlyCollection<int> clientCaseIds, CancellationToken ct)
    {
        var collection = _db.Entry(payment).Collection(x => x.InvoiceClientCases);
        if (!collection.IsLoaded)
        {
            await collection.LoadAsync(ct);
        }

        var targetIds = clientCaseIds.Where(x => x > 0).Distinct().ToHashSet();
        var toRemove = payment.InvoiceClientCases.Where(x => !targetIds.Contains(x.ClientCaseId)).ToList();
        foreach (var link in toRemove)
        {
            payment.InvoiceClientCases.Remove(link);
        }

        var existingIds = payment.InvoiceClientCases.Select(x => x.ClientCaseId).ToHashSet();
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

        payment.ClientCaseId = targetIds.Count > 0 ? targetIds.First() : null;
    }

    private async Task SyncInvoiceLineItemsAsync(Payment payment, List<InvoiceLineItemRequest>? lineItems, CancellationToken ct)
    {
        if (lineItems is null)
        {
            return;
        }

        var collection = _db.Entry(payment).Collection(x => x.InvoiceLineItems);
        if (!collection.IsLoaded)
        {
            await collection.LoadAsync(ct);
        }

        foreach (var existing in payment.InvoiceLineItems.ToList())
        {
            payment.InvoiceLineItems.Remove(existing);
        }

        var normalizedItems = InvoiceLineItemStorage.Normalize(lineItems);
        for (var index = 0; index < normalizedItems.Count; index++)
        {
            var item = normalizedItems[index];
            payment.InvoiceLineItems.Add(new InvoiceLineItem
            {
                PaymentId = payment.Id,
                ServiceId = item.ServiceId,
                SortOrder = index,
                Name = item.Name,
                Quantity = item.Quantity,
                Unit = item.Unit,
                Price = item.Price,
                CreatedAt = DateTime.UtcNow,
            });
        }

        payment.SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes);
    }

    private async Task SyncInvoiceActsAsync(Payment payment, IReadOnlyCollection<int> actIds, string invoiceNumber, CancellationToken ct, string? previousInvoiceNumber = null)
    {
        var normalizedCurrentNumber = Normalize(invoiceNumber);
        var invoiceNumbers = new[] { normalizedCurrentNumber, Normalize(previousInvoiceNumber) }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var targetIds = actIds.Where(x => x > 0).Distinct().ToHashSet();
        var currentActs = await _db.Acts
            .Where(a => a.InvoiceId == payment.Id
                || (a.InvoiceId == null && a.InvoiceNumber != null && invoiceNumbers.Contains(a.InvoiceNumber)))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var act in currentActs.Where(a => !targetIds.Contains(a.Id)))
        {
            act.InvoiceId = null;
            if (!string.IsNullOrWhiteSpace(act.InvoiceNumber) && invoiceNumbers.Contains(act.InvoiceNumber))
            {
                act.InvoiceNumber = null;
            }
            act.UpdatedAt = now;
        }

        if (targetIds.Count == 0)
        {
            return;
        }

        var selectedActs = await _db.Acts
            .Where(a => targetIds.Contains(a.Id))
            .ToListAsync(ct);

        foreach (var act in selectedActs)
        {
            act.InvoiceId = payment.Id;
            act.InvoiceNumber = normalizedCurrentNumber;
            act.ClientId = payment.ClientId;
            act.LegalEntityId = payment.LegalEntityId;
            act.UpdatedAt = now;
        }
    }

    private async Task<bool> EnsureResponsibleIdsExistAsync(HashSet<int> responsibleIds, CancellationToken ct)
    {
        if (responsibleIds.Count == 0)
        {
            return true;
        }

        var existingCount = await _db.Users
            .AsNoTracking()
            .CountAsync(u => responsibleIds.Contains(u.Id), ct);

        return existingCount == responsibleIds.Count;
    }

    private async Task<HashSet<int>> ResolveInvoiceResponsibleIdsAsync(int? clientId, int? legalEntityId, List<int>? responsibleIds, CancellationToken ct)
    {
        var normalized = NormalizeResponsibleIds(responsibleIds);
        if (normalized.Count > 0)
        {
            return normalized;
        }

        int? clientResponsibleUserId = null;
        if (clientId.HasValue && clientId.Value > 0)
        {
            clientResponsibleUserId = await _db.Clients
                .AsNoTracking()
                .Where(c => c.Id == clientId.Value)
                .Select(c => c.ResponsibleUserId)
                .FirstOrDefaultAsync(ct);
        }
        else if (legalEntityId.HasValue && legalEntityId.Value > 0)
        {
            clientResponsibleUserId = await _db.LegalEntities
                .AsNoTracking()
                .Where(entity => entity.Id == legalEntityId.Value)
                .Select(entity => entity.ResponsibleUserId)
                .FirstOrDefaultAsync(ct);
        }
        if (clientResponsibleUserId.HasValue && clientResponsibleUserId.Value > 0)
        {
            return new HashSet<int> { clientResponsibleUserId.Value };
        }

        var currentUserId = GetUserId(User);
        if (currentUserId.HasValue && currentUserId.Value > 0)
        {
            return new HashSet<int> { currentUserId.Value };
        }

        return normalized;
    }

    private static HashSet<int> NormalizeResponsibleIds(List<int>? responsibleIds)
    {
        return responsibleIds is null
            ? new HashSet<int>()
            : responsibleIds.Where(x => x > 0).ToHashSet();
    }

    private async Task<int> GetNextInvoiceNumberAsync(DateTime targetDate, int startNumber, string allocationStrategy, CancellationToken ct)
    {
        var yearStart = new DateTime(targetDate.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        var numbers = await BuildActiveInvoiceEntitiesQuery()
            .Where(x => x.Date >= yearStart && x.Date < yearEnd)
            .Select(x => x.Number)
            .ToListAsync(ct);

        var usedNumbers = numbers
            .Select(value => int.TryParse(value.Trim(), out var parsed) ? parsed : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Where(value => value >= startNumber)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (allocationStrategy == "first-free")
        {
            var candidate = startNumber;
            var usedSet = usedNumbers.ToHashSet();
            while (usedSet.Contains(candidate))
            {
                candidate++;
            }

            return candidate;
        }

        var max = usedNumbers.Count > 0 ? usedNumbers.Max() : startNumber - 1;
        return Math.Max(startNumber, max + 1);
    }

    private async Task<string?> ResolveInvoiceNumberForSaveAsync(string number, DateTime accountDate, int? excludeId, bool allowDuplicateOnImport, CancellationToken ct)
    {
        if (allowDuplicateOnImport)
        {
            var reservedNumbers = await LoadReservedInvoiceNumberLookupAsync(accountDate, excludeId, ct);
            return ResolveAvailableImportInvoiceNumber(number, reservedNumbers);
        }

        var duplicateError = await EnsureInvoiceNumberIsUniqueAsync(number, accountDate, excludeId, ct);
        return duplicateError is null ? number : null;
    }

    private static string? ResolveAvailableImportInvoiceNumber(string number, IReadOnlySet<string> reservedNumbers)
    {
        if (!reservedNumbers.Contains(BuildInvoiceNumberLookupKey(number)))
        {
            return number;
        }

        for (var i = 1; i <= 9999; i++)
        {
            var suffix = $"-IMP{i}";
            var maxBaseLength = Math.Max(1, 120 - suffix.Length);
            var truncatedBase = number.Length > maxBaseLength
                ? number[..maxBaseLength]
                : number;
            var candidate = $"{truncatedBase}{suffix}";
            if (!reservedNumbers.Contains(BuildInvoiceNumberLookupKey(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<HashSet<string>> LoadReservedInvoiceNumberLookupAsync(DateTime accountDate, int? excludeId, CancellationToken ct)
    {
        var yearStart = new DateTime(accountDate.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        var numbers = await BuildActiveInvoiceEntitiesQuery()
            .Where(x => x.Date >= yearStart && x.Date < yearEnd)
            .Where(x => excludeId == null || !x.Payments.Any(p => p.Id == excludeId.Value))
            .Select(x => x.Number)
            .ToListAsync(ct);

        return numbers
            .Select(BuildInvoiceNumberLookupKey)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildInvoiceNumberLookupKey(string? number)
    {
        return (number ?? string.Empty).Trim();
    }

    private async Task<ActionResult?> EnsureInvoiceNumberIsUniqueAsync(string number, DateTime accountDate, int? excludeId, CancellationToken ct)
    {
        var yearStart = new DateTime(accountDate.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        var normalized = number.Trim();

        var duplicateExists = await BuildActiveInvoiceEntitiesQuery()
            .Where(x => x.Date >= yearStart && x.Date < yearEnd)
            .Where(x => excludeId == null || !x.Payments.Any(p => p.Id == excludeId.Value))
            .AnyAsync(x => x.Number.Trim().ToLower() == normalized.ToLower(), ct);

        if (!duplicateExists)
        {
            return null;
        }

        return BadRequest($"Номер счета '{number}' уже занят в {accountDate.Year} году.");
    }

    private IQueryable<Invoice> BuildActiveInvoiceEntitiesQuery()
    {
        return _db.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Payments.Any());
    }

    private async Task<InvoiceRulesSettingsDto> GetInvoiceRulesSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(x => x.Key == InvoiceMaxClientCasesSettingKey || x.Key == InvoiceMaxActsSettingKey)
            .ToListAsync(ct);

        return new InvoiceRulesSettingsDto
        {
            MaxClientCasesPerInvoice = ParsePositiveSetting(settings.FirstOrDefault(x => x.Key == InvoiceMaxClientCasesSettingKey)?.Value, 1),
            MaxActsPerInvoice = ParsePositiveSetting(settings.FirstOrDefault(x => x.Key == InvoiceMaxActsSettingKey)?.Value, 1),
        };
    }

    private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private async Task<(int StartNumber, string AllocationStrategy)> GetInvoiceNumberingSettingsAsync(int? userId, CancellationToken ct)
    {
        if (!userId.HasValue || userId.Value <= 0)
        {
            return (1, "max-plus-one");
        }

        var settings = await _db.UserSettings
            .AsNoTracking()
            .Where(x => x.UserId == userId.Value && (x.Key == InvoiceNumberStartSettingKey || x.Key == InvoiceNumberStrategySettingKey))
            .ToListAsync(ct);

        return (
            ParseStartNumber(settings.FirstOrDefault(x => x.Key == InvoiceNumberStartSettingKey)?.Value),
            ParseAllocationStrategy(settings.FirstOrDefault(x => x.Key == InvoiceNumberStrategySettingKey)?.Value)
        );
    }

    private async Task<string> ResolveMigrationInvoiceNumberAsync(
        string originalNumber,
        DateTime invoiceDate,
        int startNumber,
        string allocationStrategy,
        ISet<string> reservedInvoiceNumbers,
        CancellationToken ct)
    {
        var normalized = Normalize(originalNumber) ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            return await GetNextAvailableMigrationInvoiceNumberAsync(invoiceDate, startNumber, allocationStrategy, reservedInvoiceNumbers, ct);
        }

        var reservedKey = BuildReservedInvoiceNumberKey(normalized, invoiceDate);
        if (!reservedInvoiceNumbers.Contains(reservedKey)
            && await EnsureInvoiceNumberIsUniqueAsync(normalized, invoiceDate, null, ct) is null)
        {
            return normalized;
        }

        if (int.TryParse(normalized, out _))
        {
            return await GetNextAvailableMigrationInvoiceNumberAsync(invoiceDate, startNumber, allocationStrategy, reservedInvoiceNumbers, ct);
        }

        for (var i = 1; i <= 9999; i++)
        {
            var suffix = $"-IMP{i}";
            var maxBaseLength = Math.Max(1, 120 - suffix.Length);
            var truncatedBase = normalized.Length > maxBaseLength
                ? normalized[..maxBaseLength]
                : normalized;
            var candidate = $"{truncatedBase}{suffix}";
            if (reservedInvoiceNumbers.Contains(BuildReservedInvoiceNumberKey(candidate, invoiceDate)))
            {
                continue;
            }

            if (await EnsureInvoiceNumberIsUniqueAsync(candidate, invoiceDate, null, ct) is null)
            {
                return candidate;
            }
        }

        return await GetNextAvailableMigrationInvoiceNumberAsync(invoiceDate, startNumber, allocationStrategy, reservedInvoiceNumbers, ct);
    }

    private async Task<string> GetNextAvailableMigrationInvoiceNumberAsync(
        DateTime invoiceDate,
        int startNumber,
        string allocationStrategy,
        ISet<string> reservedInvoiceNumbers,
        CancellationToken ct)
    {
        var next = await GetNextInvoiceNumberAsync(invoiceDate, startNumber, allocationStrategy, ct);
        while (reservedInvoiceNumbers.Contains(BuildReservedInvoiceNumberKey(next.ToString(), invoiceDate)))
        {
            next++;
        }

        return next.ToString();
    }

    private static string BuildReservedInvoiceNumberKey(string number, DateTime invoiceDate)
    {
        return $"{invoiceDate.Year}::{Normalize(number) ?? string.Empty}";
    }

    private static void AssignLegacyAccountSource(Payment payment, string number, DateTime invoiceDate)
    {
        var normalizedNumber = Normalize(number);
        if (normalizedNumber is null)
        {
            return;
        }

        payment.LegacyAccountNumber = normalizedNumber;
        payment.LegacyAccountDate = invoiceDate.Date;
    }

    private void RepairConvertedLegacyInstallmentAggregate(
        IReadOnlyCollection<Payment> payments,
        Payment representative,
        int invoiceEntityId,
        Invoice? representativeInvoice,
        InvoiceLegacyInstallmentRepairSummaryDto summary,
        bool dryRun,
        DateTime localToday)
    {
        var invoiceNumber = Normalize(representativeInvoice?.Number)
            ?? Normalize(representative.Account)
            ?? string.Empty;
        var invoiceDate = representativeInvoice?.Date.Date
            ?? representative.AccountDate?.Date
            ?? representative.Date.Date;
        var generatedInstallments = payments
            .Where(payment => payment.Id != representative.Id
                              && payment.DocumentKind != DocumentKind.Invoice
                              && InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment))
            .OrderBy(ResolveLegacyInstallmentDate)
            .ThenBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .ToList();
        var existingGeneratedTotal = generatedInstallments.Sum(payment => payment.Amount);
        var effectiveClientId = representative.ClientId
            ?? generatedInstallments.FirstOrDefault(payment => payment.ClientId.HasValue && payment.ClientId.Value > 0)?.ClientId;
        var effectiveClientCaseId = representative.ClientCaseId is > 0
            ? representative.ClientCaseId
            : generatedInstallments.FirstOrDefault(payment => payment.ClientCaseId.HasValue && payment.ClientCaseId.Value > 0)?.ClientCaseId;

        var missingPayment = CreateConvertedLegacyInstallmentPayment(
            representative,
            invoiceEntityId,
            invoiceNumber,
            invoiceDate,
            effectiveClientId,
            effectiveClientCaseId);
        var resultingInstallments = generatedInstallments
            .Append(missingPayment)
            .OrderBy(ResolveLegacyInstallmentDate)
            .ThenBy(payment => payment.Date)
            .ThenBy(payment => payment.Id == 0 ? int.MaxValue : payment.Id)
            .ToList();
        var resultingTotal = resultingInstallments.Sum(payment => payment.Amount);
        summary.ChildPayments = generatedInstallments.Count;
        summary.ChildPaymentsTotal = existingGeneratedTotal;
        summary.MissingInstallmentAmount = representative.Amount;
        summary.ResultingInvoiceAmount = resultingTotal;
        summary.ResultingChildPaymentsTotal = resultingTotal;
        summary.RemainingUnscheduledAmount = representative.Amount;
        summary.CreatedPayments = 1;
        summary.Actions.Add($"Создать платеж рассрочки на {representative.Amount:0.00} по строке счета #{representative.Id}.");

        foreach (var payment in generatedInstallments)
        {
            if (NormalizeLegacyRepairPaymentLinks(
                    payment,
                    invoiceEntityId,
                    invoiceNumber,
                    invoiceDate,
                    effectiveClientId,
                    effectiveClientCaseId))
            {
                summary.NormalizedLinks++;
            }
        }

        var installmentPlan = BuildLegacyRepairInstallmentPlan(resultingTotal, resultingInstallments, localToday);
        for (var index = 0; index < resultingInstallments.Count; index++)
        {
            var payment = resultingInstallments[index];
            var item = installmentPlan.Items[index];
            var mergedSystemNotes = MergeInstallmentMetadataIntoSystemNotes(payment.SystemNotes, installmentPlan, item);
            if (!string.Equals(payment.SystemNotes, mergedSystemNotes, StringComparison.Ordinal))
            {
                payment.SystemNotes = mergedSystemNotes;
                summary.AddedMarkers++;
            }

            PaymentDomainService.Normalize(payment);
        }

        if (!AreAmountsEqual(representative.Amount, resultingTotal))
        {
            representative.Amount = resultingTotal;
            summary.UpdatedInvoiceAmounts++;
            summary.Actions.Add($"Обновить сумму счета #{representative.Id} до {resultingTotal:0.00}.");
        }

        var beforeState = InvoicePaymentStateService.CaptureState(representative);
        InvoicePaymentStateService.ApplySnapshot(
            representative,
            InvoicePaymentStateService.CalculateRepresentativeState(representative, resultingInstallments, DateTime.UtcNow));
        var afterState = InvoicePaymentStateService.CaptureState(representative);
        if (!AreAmountsEqual(beforeState.PaidAmount, afterState.PaidAmount))
        {
            summary.UpdatedInvoicePaidAmounts++;
        }

        if (effectiveClientId.HasValue
            && effectiveClientId.Value > 0
            && SyncInvoiceMigrationCaseLinks(representative, effectiveClientId.Value, effectiveClientCaseId))
        {
            summary.UpdatedCaseLinks++;
        }

        summary.Repaired = summary.CreatedPayments > 0
            || summary.UpdatedInvoiceAmounts > 0
            || summary.UpdatedInvoicePaidAmounts > 0
            || summary.AddedMarkers > 0
            || summary.NormalizedLinks > 0
            || summary.UpdatedCaseLinks > 0;

        if (!dryRun)
        {
            _db.Payments.Add(missingPayment);
        }
    }

    private static Payment CreateConvertedLegacyInstallmentPayment(
        Payment representative,
        int invoiceEntityId,
        string invoiceNumber,
        DateTime invoiceDate,
        int? effectiveClientId,
        int? effectiveClientCaseId)
    {
        var payment = new Payment
        {
            Account = invoiceNumber,
            AccountDate = invoiceDate,
            Amount = representative.Amount,
            ClientCaseId = effectiveClientCaseId,
            ClientCaseReference = representative.ClientCaseReference,
            ClientId = effectiveClientId ?? representative.ClientId,
            LegalEntityId = representative.LegalEntityId,
            ContractId = representative.ContractId,
            CounterpartyName = representative.CounterpartyName,
            Date = representative.Date,
            DealTypeId = representative.DealTypeId,
            Description = representative.Description,
            DocumentKind = DocumentKind.Payment,
            ExpenseClientId = representative.ExpenseClientId,
            ExpenseLegalEntityId = representative.ExpenseLegalEntityId,
            IncomeTypeId = representative.IncomeTypeId,
            InvoiceId = invoiceEntityId,
            IsNotForAccounting = representative.IsNotForAccounting,
            IsPaid = representative.IsPaid,
            LastPaymentDate = representative.LastPaymentDate,
            Notes = representative.Notes,
            PaidAmount = representative.PaidAmount,
            PaidDate = representative.PaidDate,
            PaymentSourceId = representative.PaymentSourceId,
            PaymentStatusId = representative.PaymentStatusId,
            PlannedDate = representative.PlannedDate == default ? representative.Date : representative.PlannedDate,
            RescheduleCount = representative.RescheduleCount,
            ServiceId = representative.ServiceId,
            Status = representative.Status,
            InvoiceWorkflowStatus = representative.InvoiceWorkflowStatus,
            SystemNotes = representative.SystemNotes,
            Type = representative.Type,
        };

        foreach (var responsibleId in representative.Responsibles.Select(item => item.UserId).Distinct())
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }

        PaymentDomainService.Normalize(payment);
        return payment;
    }

    private static bool NormalizeLegacyRepairPaymentLinks(
        Payment payment,
        int invoiceEntityId,
        string invoiceNumber,
        DateTime invoiceDate,
        int? effectiveClientId,
        int? effectiveClientCaseId)
    {
        var normalized = false;
        if (payment.InvoiceId != invoiceEntityId)
        {
            payment.InvoiceId = invoiceEntityId;
            normalized = true;
        }

        if (!string.Equals(Normalize(payment.Account), invoiceNumber, StringComparison.OrdinalIgnoreCase))
        {
            payment.Account = invoiceNumber;
            normalized = true;
        }

        if (payment.AccountDate?.Date != invoiceDate.Date)
        {
            payment.AccountDate = invoiceDate.Date;
            normalized = true;
        }

        if (effectiveClientId.HasValue && payment.ClientId != effectiveClientId)
        {
            payment.ClientId = effectiveClientId;
            normalized = true;
        }

        if (effectiveClientCaseId.HasValue && payment.ClientCaseId != effectiveClientCaseId)
        {
            payment.ClientCaseId = effectiveClientCaseId;
            normalized = true;
        }

        return normalized;
    }

    private static bool IsConvertedLegacyInstallmentAggregate(IReadOnlyCollection<Payment> payments, Payment representative)
    {
        if (payments.Count < 3 || representative.Amount <= 0m)
        {
            return false;
        }

        var generatedInstallments = payments
            .Where(payment => payment.Id != representative.Id
                              && payment.DocumentKind != DocumentKind.Invoice
                              && InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment))
            .ToList();
        if (generatedInstallments.Count < 2)
        {
            return false;
        }

        var generatedTotal = generatedInstallments.Sum(payment => payment.Amount);
        return generatedTotal > representative.Amount
               && !HasConsistentInstallmentToPay(
                   generatedInstallments.Select(payment => payment.SystemNotes),
                   generatedTotal);
    }

    private static bool IsLegacyBrokenInstallmentAggregate(IReadOnlyCollection<Payment> payments)
    {
        if (payments.Count < 3 || payments.Any(payment => InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment)))
        {
            return false;
        }

        var invoiceRows = payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .ToList();
        if (invoiceRows.Count < 2)
        {
            return false;
        }

        var nonInvoiceCount = payments.Count - invoiceRows.Count;
        if (nonInvoiceCount > 1)
        {
            return false;
        }

        var representative = SelectRepresentativeInvoicePayment(payments);
        if (representative is null)
        {
            return false;
        }

        var maxSiblingAmount = payments
            .Where(payment => payment.Id != representative.Id)
            .Select(payment => payment.Amount)
            .DefaultIfEmpty(0m)
            .Max();

        return representative.Amount > maxSiblingAmount;
    }

    private static Payment? SelectRepresentativeInvoicePayment(IEnumerable<Payment> payments)
    {
        return payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(payment => payment.Amount)
            .ThenBy(payment => payment.Id)
            .FirstOrDefault();
    }

    private static InvoiceInstallmentPlanRequest BuildLegacyRepairInstallmentPlan(
        decimal representativeAmount,
        IReadOnlyList<Payment> childPayments,
        DateTime localToday)
    {
        var totalAmount = representativeAmount > 0m
            ? representativeAmount
            : childPayments.Sum(payment => payment.Amount);
        var startDate = childPayments.Count == 0
            ? localToday.Date
            : ResolveLegacyInstallmentDate(childPayments[0]);
        var runningAmount = 0m;
        var items = new List<InvoiceInstallmentPlanItemRequest>(childPayments.Count);

        for (var index = 0; index < childPayments.Count; index++)
        {
            var payment = childPayments[index];
            runningAmount += Math.Max(payment.Amount, 0m);
            items.Add(new InvoiceInstallmentPlanItemRequest
            {
                Sequence = index + 1,
                Date = ResolveLegacyInstallmentDate(payment),
                Principal = Math.Max(payment.Amount, 0m),
                Interest = 0m,
                Payment = Math.Max(payment.Amount, 0m),
                Balance = Math.Max(totalAmount - runningAmount, 0m),
            });
        }

        return new InvoiceInstallmentPlanRequest
        {
            Total = totalAmount,
            DownPayment = 0m,
            AnnualRate = 0m,
            Months = Math.Max(items.Count, 1),
            StartDate = startDate,
            RoundingMode = InstallmentRoundingMode.None,
            RoundingStep = null,
            ToPay = totalAmount,
            Overpay = 0m,
            LoanAmount = totalAmount,
            BaseMonthlyPayment = items.Count > 0
                ? Math.Round(totalAmount / items.Count, 2, MidpointRounding.AwayFromZero)
                : null,
            RoundedMonthlyPayment = null,
            Items = items,
        };
    }

    private static DateTime ResolveLegacyInstallmentDate(Payment payment)
    {
        return (payment.PlannedDate == default ? payment.Date : payment.PlannedDate).Date;
    }

    private async Task UpsertUserSettingAsync(int userId, string key, string value, DateTime nowUtc, CancellationToken ct)
    {
        var setting = await _db.UserSettings
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Key == key, ct);

        if (setting is null)
        {
            _db.UserSettings.Add(new UserSetting
            {
                UserId = userId,
                Key = key,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private static int ParseStartNumber(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 1;
    }

    private static int ParsePositiveSetting(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string ParseAllocationStrategy(string? value)
    {
        return string.Equals(value, "first-free", StringComparison.OrdinalIgnoreCase)
            ? "first-free"
            : "max-plus-one";
    }

    private static int? ResolveVisibleResponsibleId(int? requestedResponsibleId, DataVisibilityAccess visibility)
    {
        if (visibility.SeesAll)
        {
            return requestedResponsibleId;
        }

        if (requestedResponsibleId.HasValue)
        {
            return visibility.CanSeeUser(requestedResponsibleId.Value)
                ? requestedResponsibleId
                : -1;
        }

        return null;
    }

    private static int? GetUserId(ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (int?)null;

    private static string NormalizeDocumentFormat(string? format)
    {
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            return "html";
        }

        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "pdf";
        }

        return "word";
    }

    private static string BuildInvoiceHtml(Payment payment, IReadOnlyCollection<InvoiceLineItemDto> lineItems)
    {
        var number = WebUtility.HtmlEncode(payment.Invoice?.Number ?? payment.Account ?? "—");
        var date = (payment.Invoice?.Date ?? payment.AccountDate ?? payment.Date).ToString("dd.MM.yyyy");
        var clientName = WebUtility.HtmlEncode(payment.Client?.Name ?? "—");
        var amount = payment.Amount.ToString("0.00");

        var rowsBuilder = new StringBuilder();
        var index = 1;
        foreach (var item in lineItems)
        {
            var total = item.Quantity * item.Price;
            rowsBuilder.Append("<tr>");
            rowsBuilder.Append($"<td>{index}</td>");
            rowsBuilder.Append($"<td>{WebUtility.HtmlEncode(item.Name)}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{item.Quantity:0.##}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{WebUtility.HtmlEncode(item.Unit)}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{item.Price:0.00}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{total:0.00}</td>");
            rowsBuilder.Append("</tr>");
            index++;
        }

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ru\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\" />");
        html.AppendLine($"  <title>Счёт № {number}</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: Arial, sans-serif; color: #111827; margin: 24px; }");
        html.AppendLine("    table { border-collapse: collapse; width: 100%; margin-top: 12px; }");
        html.AppendLine("    th, td { border: 1px solid #1f2937; padding: 6px 8px; font-size: 12px; }");
        html.AppendLine("    .num { text-align: right; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"  <h1>Счёт № {number} от {date}</h1>");
        html.AppendLine($"  <p><b>Кому:</b> {clientName}</p>");
        html.AppendLine("  <table>");
        html.AppendLine("    <thead>");
        html.AppendLine("      <tr><th>№</th><th>Товары (работы, услуги)</th><th class=\"num\">Кол-во</th><th class=\"num\">Ед.</th><th class=\"num\">Цена</th><th class=\"num\">Сумма</th></tr>");
        html.AppendLine("    </thead>");
        html.AppendLine("    <tbody>");
        html.AppendLine(rowsBuilder.ToString());
        html.AppendLine("    </tbody>");
        html.AppendLine("  </table>");
        html.AppendLine($"  <p><b>Итого:</b> {amount}</p>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private async Task<Payment?> LoadInvoiceDocumentPaymentAsync(int id, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return null;
        }

        return await _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .Include(p => p.Invoice)
            .Include(p => p.Client)
                .ThenInclude(client => client!.Addresses)
            .Include(p => p.Client)
                .ThenInclude(client => client!.Passports)
            .Include(p => p.Client)
                .ThenInclude(client => client!.LegalEntity)
                    .ThenInclude(entity => entity!.Addresses)
            .Include(p => p.LegalEntity)
                .ThenInclude(entity => entity!.Addresses)
            .Include(p => p.ExpenseClient)
                .ThenInclude(client => client!.Addresses)
            .Include(p => p.ExpenseClient)
                .ThenInclude(client => client!.Passports)
            .Include(p => p.ExpenseClient)
                .ThenInclude(client => client!.LegalEntity)
                    .ThenInclude(entity => entity!.Addresses)
            .Include(p => p.ExpenseLegalEntity)
                .ThenInclude(entity => entity!.Addresses)
            .Include(p => p.Contract)
            .Include(p => p.ClientCase)
            .Include(p => p.InvoiceClientCases)
                .ThenInclude(link => link.ClientCase)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    private async Task<string> BuildInstallmentScheduleHtmlAsync(Payment payment, CancellationToken ct)
    {
        var scheduleContext = await LoadInstallmentScheduleContextAsync(payment, ct);
        return RenderInstallmentScheduleHtml(scheduleContext.Items);
    }

    private async Task<InvoiceDocumentInstallmentContext> BuildInstallmentDocumentContextAsync(
        Payment payment,
        List<InvoiceLineItemDto> lineItems,
        CancellationToken ct)
    {
        var scheduleContext = await LoadInstallmentScheduleContextAsync(payment, ct);
        var effectiveLineItems = lineItems;

        if (effectiveLineItems.Count == 0
            && scheduleContext.InferredTotal.HasValue
            && scheduleContext.InferredTotal.Value > payment.Amount)
        {
            effectiveLineItems = new List<InvoiceLineItemDto>
            {
                new()
                {
                    SourceKey = "legacy-installment-total",
                    SortOrder = 0,
                    Name = string.IsNullOrWhiteSpace(payment.Description)
                        ? "Услуги по счету"
                        : payment.Description,
                    Quantity = 1m,
                    Unit = "усл. ед.",
                    Price = scheduleContext.InferredTotal.Value,
                },
            };
        }

        return new InvoiceDocumentInstallmentContext(
            effectiveLineItems,
            RenderInstallmentScheduleHtml(scheduleContext.Items));
    }

    private async Task<InstallmentScheduleContext> LoadInstallmentScheduleContextAsync(Payment payment, CancellationToken ct)
    {
        var invoiceId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (!invoiceId.HasValue)
        {
            return new InstallmentScheduleContext(new List<InstallmentScheduleItem>(), null);
        }

        var invoicePayments = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.InvoiceId == invoiceId.Value)
            .Select(p => new { p.Id, p.Date, p.PlannedDate, p.Amount, p.DocumentKind, p.SystemNotes })
            .ToListAsync(ct);

        var generatedInstallments = invoicePayments
            .Where(item =>
                item.Id != payment.Id
                && !string.IsNullOrWhiteSpace(item.SystemNotes)
                && item.SystemNotes.Contains(InstallmentPaymentMarker, StringComparison.Ordinal))
            .ToList();

        var items = new List<InstallmentScheduleItem>();
        foreach (var installment in generatedInstallments)
        {
            var metadata = ExtractInstallmentPaymentMetadata(installment.SystemNotes);
            var metadataItem = metadata?.Item;
            var date = metadataItem is not null && metadataItem.Date != default ? metadataItem.Date : installment.Date;
            var amount = metadataItem is not null && metadataItem.Payment > 0m ? metadataItem.Payment : installment.Amount;
            var sequence = metadataItem?.Sequence ?? 0;
            items.Add(new InstallmentScheduleItem(sequence, date, amount, installment.Id));
        }

        decimal? inferredTotal = null;
        var generatedInstallmentTotal = items.Sum(item => item.Amount);
        if (payment.DocumentKind == DocumentKind.Invoice
            && payment.Amount > 0m
            && IsConvertedLegacyInstallmentSchedule(
                generatedInstallments.Select(item => item.SystemNotes),
                generatedInstallmentTotal,
                payment.Amount))
        {
            var currentDate = payment.PlannedDate == default ? payment.Date : payment.PlannedDate;
            var nextSequence = items
                .Select(item => item.Sequence)
                .DefaultIfEmpty(0)
                .Max() + 1;
            items.Add(new InstallmentScheduleItem(nextSequence, currentDate, payment.Amount, payment.Id));
            inferredTotal = generatedInstallmentTotal + payment.Amount;
        }

        return new InstallmentScheduleContext(items, inferredTotal);
    }

    private static bool IsConvertedLegacyInstallmentSchedule(
        IEnumerable<string?> generatedInstallmentNotes,
        decimal generatedInstallmentTotal,
        decimal representativeAmount)
    {
        var notes = generatedInstallmentNotes.ToList();
        if (notes.Count < 2 || representativeAmount <= 0m || generatedInstallmentTotal <= representativeAmount)
        {
            return false;
        }

        return !HasConsistentInstallmentToPay(notes, generatedInstallmentTotal);
    }

    private static bool HasConsistentInstallmentToPay(IEnumerable<string?> systemNotes, decimal expectedTotal)
        => systemNotes
            .Select(ExtractInstallmentPaymentMetadata)
            .Any(metadata => metadata is not null && AreAmountsEqual(metadata.ToPay, expectedTotal));

    private static bool AreAmountsEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= 0.01m;

    private static string RenderInstallmentScheduleHtml(IReadOnlyCollection<InstallmentScheduleItem> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var orderedItems = items
            .OrderBy(item => item.Sequence == 0 ? int.MaxValue : item.Sequence)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Id)
            .ToList();

        var builder = new StringBuilder();
        builder.Append("<div class=\"installment-schedule\">");
        builder.Append("<div class=\"installment-title\">ГРАФИК ПЛАТЕЖЕЙ:</div>");
        var index = 1;
        foreach (var item in orderedItems)
        {
            var amountText = item.Amount.ToString("N0", RuCulture);
            builder.Append($"<div class=\"installment-item\">{index}. {item.Date:dd.MM.yyyy} - {amountText} руб.</div>");
            index++;
        }
        builder.Append("</div>");

        return builder.ToString();
    }

    private async Task<LegalEntity?> LoadMainCompanyAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting =>
                setting.Key == LegacyMainCompanySettingKey
                || setting.Key == MainCompanyIdsSettingKey
                || setting.Key == DefaultMainCompanyIdSettingKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);

        int? companyId = null;

        if (settings.TryGetValue(DefaultMainCompanyIdSettingKey, out var rawDefaultId)
            && int.TryParse(rawDefaultId, out var parsedDefaultId)
            && parsedDefaultId > 0)
        {
            companyId = parsedDefaultId;
        }
        else if (settings.TryGetValue(LegacyMainCompanySettingKey, out var rawLegacyId)
                 && int.TryParse(rawLegacyId, out var parsedLegacyId)
                 && parsedLegacyId > 0)
        {
            companyId = parsedLegacyId;
        }
        else if (settings.TryGetValue(MainCompanyIdsSettingKey, out var rawIds)
                 && !string.IsNullOrWhiteSpace(rawIds))
        {
            try
            {
                var parsedIds = JsonSerializer.Deserialize<List<int>>(rawIds);
                companyId = parsedIds?.FirstOrDefault(id => id > 0);
            }
            catch (JsonException)
            {
                companyId = null;
            }
        }

        if (!companyId.HasValue || companyId.Value <= 0)
        {
            return null;
        }

        return await _db.LegalEntities
            .AsNoTracking()
            .Include(entity => entity.Addresses)
            .FirstOrDefaultAsync(entity => entity.Id == companyId.Value, ct);
    }

    private async Task<InvoiceDocumentTemplateSettingsDto> GetInvoiceDocumentTemplateSettingsAsync(CancellationToken ct)
    {
        var rawJson = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == InvoiceDocumentTemplatesSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        var storage = InvoiceDocumentTemplateEngine.Deserialize(rawJson);
        return storage is null
            ? InvoiceDocumentTemplateEngine.CreateDefaultSettings()
            : InvoiceDocumentTemplateEngine.CreateResponse(storage);
    }

    private async Task<InvoiceDocumentTemplateDto> GetInvoiceDocumentTemplateAsync(string? preferredTemplateKey, CancellationToken ct)
    {
        var settings = await GetInvoiceDocumentTemplateSettingsAsync(ct);
        return SelectInvoiceDocumentTemplate(settings, preferredTemplateKey);
    }

    private static InvoiceDocumentTemplateDto SelectInvoiceDocumentTemplate(
        InvoiceDocumentTemplateSettingsDto settings,
        string? preferredTemplateKey)
    {
        if (!string.IsNullOrWhiteSpace(preferredTemplateKey))
        {
            var preferred = settings.Templates.FirstOrDefault(template =>
                string.Equals(template.Key, preferredTemplateKey.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return settings.Templates.FirstOrDefault(template =>
                   string.Equals(template.Key, settings.ActiveTemplateKey, StringComparison.OrdinalIgnoreCase))
               ?? settings.Templates[0];
    }

    private async Task RememberInvoiceDocumentTemplateAsync(Payment payment, string templateKey, CancellationToken ct)
    {
        if (string.Equals(payment.InvoiceDocumentTemplateKey, templateKey, StringComparison.Ordinal))
        {
            return;
        }

        payment.InvoiceDocumentTemplateKey = templateKey;
        await _db.SaveChangesAsync(ct);
    }

    private static string? ValidateInvoiceDocumentTemplateSettings(InvoiceDocumentTemplateEngine.InvoiceDocumentTemplateSettingsStorage storage)
    {
        if (storage.Templates.Count == 0)
        {
            return "Добавьте хотя бы один шаблон документа.";
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in storage.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.Key))
            {
                return "У каждого шаблона должен быть внутренний ключ.";
            }

            if (!keys.Add(template.Key))
            {
                return $"Шаблон с ключом '{template.Key}' уже существует.";
            }

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                return $"Заполните название шаблона '{template.Key}'.";
            }

            if (string.IsNullOrWhiteSpace(template.Html))
            {
                return $"Шаблон '{template.Name}' не может быть пустым.";
            }
        }

        if (string.IsNullOrWhiteSpace(storage.ActiveTemplateKey))
        {
            return "Выберите активный шаблон документа.";
        }

        if (!storage.Templates.Any(template => string.Equals(template.Key, storage.ActiveTemplateKey, StringComparison.OrdinalIgnoreCase)))
        {
            return "Активный шаблон должен присутствовать в списке шаблонов.";
        }

        return null;
    }

    private static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "export";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '-');
        }

        return value.Trim();
    }

    private sealed record InvoiceDocumentInstallmentContext(List<InvoiceLineItemDto> LineItems, string ScheduleHtml);

    private sealed record InstallmentScheduleContext(List<InstallmentScheduleItem> Items, decimal? InferredTotal);

    private sealed class InstallmentPaymentMetadataDto
    {
        public int Sequence { get; init; }
        public decimal Total { get; init; }
        public decimal DownPayment { get; init; }
        public decimal AnnualRate { get; init; }
        public int Months { get; init; }
        public DateTime StartDate { get; init; }
        public InstallmentRoundingMode RoundingMode { get; init; }
        public decimal? RoundingStep { get; init; }
        public decimal ToPay { get; init; }
        public decimal Overpay { get; init; }
        public decimal? LoanAmount { get; init; }
        public decimal? BaseMonthlyPayment { get; init; }
        public decimal? RoundedMonthlyPayment { get; init; }
        public InstallmentPaymentMetadataItemDto Item { get; init; } = new();
    }

    private sealed class InstallmentPaymentMetadataItemDto
    {
        public int Sequence { get; init; }
        public DateTime Date { get; init; }
        public decimal Principal { get; init; }
        public decimal Interest { get; init; }
        public decimal Payment { get; init; }
        public decimal Balance { get; init; }
    }

    private sealed record InstallmentScheduleItem(int Sequence, DateTime Date, decimal Amount, int Id);

    private sealed class InvoiceProjectionRaw
    {
        public int Id { get; init; }
        public int? InvoiceEntityId { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateTime Date { get; init; }
        public DateTime? DueDate { get; init; }
        public decimal Amount { get; init; }
        public decimal PaidAmount { get; init; }
        public PaymentType Type { get; init; }
        public PaymentStatus Status { get; init; }
        public string WorkflowStatus { get; init; } = InvoiceWorkflowStatus.Draft;
        public DateTime? SentForSigningAt { get; init; }
        public DateTime? SignedAt { get; init; }
        public DateTime? CancelledAt { get; init; }
        public int? CancelledByUserId { get; init; }
        public string? CancelledReason { get; init; }
        public bool IsPaid { get; init; }
        public DateTime? PaidDate { get; init; }
        public int? ClientId { get; init; }
        public int? LegalEntityId { get; init; }
        public int? ExpenseClientId { get; init; }
        public int? ExpenseLegalEntityId { get; init; }
        public int? ContractId { get; init; }
        public string? ClientName { get; init; }
        public string? LegalEntityName { get; init; }
        public string? LegalEntityInn { get; init; }
        public string? ClientCompany { get; init; }
        public int? ClientStatusId { get; init; }
        public ClientStatusDto? ClientStatus { get; init; }
        public string? ExpenseClientName { get; init; }
        public string? ExpenseLegalEntityName { get; init; }
        public string? ExpenseLegalEntityInn { get; init; }
        public string? ExpenseClientCompany { get; init; }
        public int? ExpenseClientStatusId { get; init; }
        public ClientStatusDto? ExpenseClientStatus { get; init; }
        public int? ClientCaseId { get; init; }
        public string? ClientCaseTitle { get; init; }
        public string? ClientCaseReference { get; init; }
        public string? Description { get; init; }
        public bool IsNotForAccounting { get; init; }
        public string? ActReference { get; init; }
        public int? ActId { get; init; }
        public string? ActNumber { get; init; }
        public string? ActTitle { get; init; }
        public ActStatus? ActStatus { get; init; }
        public string? ActWorkflowStatus { get; init; }
        public int? ResponsibleId { get; init; }
        public string? ResponsibleName { get; init; }
        public string? CounterpartyInn { get; init; }
        public string? CounterpartyName { get; init; }
        public int? ServiceId { get; init; }
        public string? ServiceName { get; init; }
        public string? PaymentStatusName { get; init; }
        public DateTime CreatedAt { get; init; }
        public int? AuditCreatedByUserId { get; init; }
        public string? AuditCreatedByUserName { get; init; }
        public DateTime? AuditUpdatedAt { get; init; }
        public int? AuditUpdatedByUserId { get; init; }
        public string? AuditUpdatedByUserName { get; init; }
        public bool AuditIsDeleted { get; init; }
        public DateTime? AuditDeletedAt { get; init; }
        public int? AuditDeletedByUserId { get; init; }
        public string? AuditDeletedByUserName { get; init; }
        public DocumentKind DocumentKind { get; init; }
        public string? SystemNotes { get; init; }
    }

    private sealed class InvoiceQueryItem
    {
        public Payment Payment { get; init; } = null!;
        public Act? Act { get; init; }
    }
}
