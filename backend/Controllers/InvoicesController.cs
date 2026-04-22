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
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;

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
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string DefaultMainCompanyIdSettingKey = "legalEntities.defaultMainCompanyId";
    private readonly PaymentContext _db;
    private readonly IEmailService _emailService;
    private readonly IFormRequirementsService _formRequirements;
    private readonly IInvoiceDocumentPdfService? _invoiceDocumentPdfService;
    private const string InstallmentPaymentMarker = "[pp:installment-payment]";
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    public InvoicesController(
        PaymentContext db,
        IEmailService emailService,
        IFormRequirementsService formRequirements,
        IInvoiceDocumentPdfService? invoiceDocumentPdfService = null)
    {
        _db = db;
        _emailService = emailService;
        _formRequirements = formRequirements;
        _invoiceDocumentPdfService = invoiceDocumentPdfService;
    }

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
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        var effectiveResponsibleId = ResolveVisibleResponsibleId(responsibleId);
        if (!User.IsInRole("admin") && !effectiveResponsibleId.HasValue)
        {
            return Unauthorized();
        }

        var query = BuildInvoiceQuery();
        query = ApplyFilters(query, from, to, status, clientId, contractId, caseId, effectiveResponsibleId, search, type, ParseIntList(clientIds));
        query = ApplySort(query, sortBy, sortDir);

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var rawItems = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(ProjectionRaw)
            .ToListAsync(ct);

        var items = rawItems.Select(MapFromRaw).ToList();
        await EnrichInvoiceDtosAsync(items, ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("summary")]
    public async Task<ActionResult<InvoiceSummaryDto>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        [FromQuery] PaymentType? type,
        CancellationToken ct = default)
    {
        var effectiveResponsibleId = ResolveVisibleResponsibleId(responsibleId);
        if (!User.IsInRole("admin") && !effectiveResponsibleId.HasValue)
        {
            return Unauthorized();
        }

        var query = BuildInvoiceQuery();
        query = ApplyFilters(query, from, to, status, clientId, null, caseId, effectiveResponsibleId, search, type);

        var today = DateTime.UtcNow.Date;

        var overdueAgingRows = await query
            .Where(x => x.Payment.Status == PaymentStatus.Overdue)
            .Select(x => new
            {
                Amount = x.Payment.Amount,
                DueDate = x.Payment.Date,
            })
            .ToListAsync(ct);

        decimal overdue0To10 = 0m;
        decimal overdue11To30 = 0m;
        decimal overdue31To60 = 0m;
        decimal overdue61To90 = 0m;
        decimal overdue91Plus = 0m;

        foreach (var row in overdueAgingRows)
        {
            var daysOverdue = Math.Max(0, (today - row.DueDate.Date).Days);
            if (daysOverdue <= 10)
            {
                overdue0To10 += row.Amount;
                continue;
            }

            if (daysOverdue <= 30)
            {
                overdue11To30 += row.Amount;
                continue;
            }

            if (daysOverdue <= 60)
            {
                overdue31To60 += row.Amount;
                continue;
            }

            if (daysOverdue <= 90)
            {
                overdue61To90 += row.Amount;
                continue;
            }

            overdue91Plus += row.Amount;
        }

        var grouped = await query
            .GroupBy(x => x.Payment.Status)
            .Select(g => new
            {
                Status = g.Key,
                Amount = g.Sum(x => x.Payment.Amount),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        var includeCancelledInTotal = status == PaymentStatus.Cancelled;
        var totalGroups = grouped.Where(x => includeCancelledInTotal || x.Status != PaymentStatus.Cancelled);
        decimal totalAmount = totalGroups.Sum(x => x.Amount);
        int totalCount = totalGroups.Sum(x => x.Count);

        var pending = grouped.FirstOrDefault(x => x.Status == PaymentStatus.Pending);
        var paid = grouped.FirstOrDefault(x => x.Status == PaymentStatus.Completed);
        var overdue = grouped.FirstOrDefault(x => x.Status == PaymentStatus.Overdue);

        var summary = new InvoiceSummaryDto
        {
            Total = new InvoiceSummaryBucketDto { Amount = totalAmount, Count = totalCount },
            Pending = new InvoiceSummaryBucketDto
            {
                Amount = pending?.Amount ?? 0m,
                Count = pending?.Count ?? 0,
            },
            Paid = new InvoiceSummaryBucketDto
            {
                Amount = paid?.Amount ?? 0m,
                Count = paid?.Count ?? 0,
            },
            Overdue = new InvoiceSummaryBucketDto
            {
                Amount = overdue?.Amount ?? 0m,
                Count = overdue?.Count ?? 0,
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
        var raw = await BuildInvoiceQuery()
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

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("contract-invoices", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var clientExists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ClientId, ct);
        if (!clientExists)
        {
            return BadRequest($"Unknown clientId {request.ClientId}");
        }

        var expenseClientValidationError = await ValidateExpenseClientAsync(request, ct);
        if (expenseClientValidationError is not null)
        {
            return expenseClientValidationError;
        }

        var contractValidationError = await ValidateContractClientLinkAsync(request.ContractId, request.ClientId, ct);
        if (contractValidationError is not null)
        {
            return contractValidationError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        number = await ResolveInvoiceNumberForSaveAsync(number, request.Date.Date, null, request.AllowDuplicateNumberOnImport, ct);
        if (string.IsNullOrEmpty(number))
        {
            var duplicateNumberError = await EnsureInvoiceNumberIsUniqueAsync(Normalize(request.Number) ?? string.Empty, request.Date.Date, null, ct);
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

        var caseOwnerClientId = ResolveInvoiceCaseOwnerClientId(request);
        var clientCaseValidation = await ValidateInvoiceClientCasesAsync(caseOwnerClientId, request, ct);
        if (clientCaseValidation.Error is not null)
        {
            return clientCaseValidation.Error;
        }

        var actValidation = await ValidateInvoiceActsAsync(request.ClientId, request, null, ct);
        if (actValidation.Error is not null)
        {
            return actValidation.Error;
        }

        var invoice = new Invoice
        {
            Number = number,
            Date = request.Date.Date,
            CreatedAt = DateTime.UtcNow,
        };

        var resolvedDueDate = ResolveInvoiceDueDate(request);

        var payment = new Payment
        {
            Account = number,
            DocumentKind = DocumentKind.Invoice,
            AccountDate = request.Date.Date,
            Invoice = invoice,
            Date = resolvedDueDate,
            Amount = request.Amount,
            Status = request.Status,
            Type = request.Type,
            Description = Normalize(request.Description) ?? string.Empty,
            IsNotForAccounting = request.IsNotForAccounting,
            ClientCaseReference = Normalize(request.ClientCaseReference),
            Notes = Normalize(request.ActReference) ?? string.Empty,
            ClientId = request.ClientId,
            ExpenseClientId = ResolveInvoiceExpenseClientId(request),
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

        payment.SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes);

        var responsibleIds = await ResolveInvoiceResponsibleIdsAsync(caseOwnerClientId, request.ResponsibleIds, ct);
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

        ApplyPaidFlags(payment, request.PaidDate);

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        await SyncInvoiceLineItemsAsync(payment, request.LineItems, ct);
        await SyncInvoiceClientCasesAsync(payment, clientCaseValidation.ClientCaseIds, ct);
        await SyncInvoiceActsAsync(payment, actValidation.ActIds, number, ct);
        var installmentSyncError = await SyncInstallmentPaymentsAsync(payment, request, responsibleIds, ct);
        if (installmentSyncError is not null)
        {
            return installmentSyncError;
        }
        await _db.SaveChangesAsync(ct);

        var dto = await LoadInvoiceDtoAsync(payment.Id, ct);

        return Created($"/api/invoices/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> Update(int id, [FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("contract-invoices", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var payment = await _db.Payments
            .Include(p => p.Responsibles)
            .Include(p => p.Invoice)
            .Include(p => p.InvoiceClientCases)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        if (!await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ClientId, ct))
        {
            return BadRequest($"Unknown clientId {request.ClientId}");
        }

        var expenseClientValidationError = await ValidateExpenseClientAsync(request, ct);
        if (expenseClientValidationError is not null)
        {
            return expenseClientValidationError;
        }

        var contractValidationError = await ValidateContractClientLinkAsync(request.ContractId, request.ClientId, ct);
        if (contractValidationError is not null)
        {
            return contractValidationError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        number = await ResolveInvoiceNumberForSaveAsync(number, request.Date.Date, id, request.AllowDuplicateNumberOnImport, ct);
        if (string.IsNullOrEmpty(number))
        {
            var duplicateNumberError = await EnsureInvoiceNumberIsUniqueAsync(Normalize(request.Number) ?? string.Empty, request.Date.Date, id, ct);
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

        var caseOwnerClientId = ResolveInvoiceCaseOwnerClientId(request);
        var clientCaseValidation = await ValidateInvoiceClientCasesAsync(caseOwnerClientId, request, ct);
        if (clientCaseValidation.Error is not null)
        {
            return clientCaseValidation.Error;
        }

        var actValidation = await ValidateInvoiceActsAsync(request.ClientId, request, id, ct);
        if (actValidation.Error is not null)
        {
            return actValidation.Error;
        }

        var previousInvoiceNumber = payment.Account;
        var originalStatus = payment.Status;
        var actor = User.GetDisplayName();
        var now = DateTime.UtcNow;

        payment.Account = number;
        payment.DocumentKind = DocumentKind.Invoice;
        payment.AccountDate = request.Date.Date;
        payment.Invoice ??= new Invoice
        {
            Number = number,
            Date = request.Date.Date,
            CreatedAt = now,
        };
        payment.Invoice.Number = number;
        payment.Invoice.Date = request.Date.Date;
        payment.Invoice.UpdatedAt = now;
        payment.InvoiceId = payment.Invoice.Id == 0 ? null : payment.Invoice.Id;
        payment.Date = ResolveInvoiceDueDate(request);
        payment.Amount = request.Amount;
        payment.Status = request.Status;
        payment.Type = request.Type;
        payment.Description = Normalize(request.Description) ?? string.Empty;
        payment.IsNotForAccounting = request.IsNotForAccounting;
        payment.ClientCaseReference = Normalize(request.ClientCaseReference);
        payment.Notes = Normalize(request.ActReference) ?? string.Empty;
        payment.ClientId = request.ClientId;
        payment.ExpenseClientId = ResolveInvoiceExpenseClientId(request);
        payment.ContractId = request.ContractId;
        payment.ClientCaseId = clientCaseValidation.ClientCaseIds.Count > 0 ? clientCaseValidation.ClientCaseIds[0] : null;
        payment.PaymentSourceId = request.PaymentSourceId;
        payment.ServiceId = request.ServiceId;
        payment.IncomeTypeId = request.IncomeTypeId;
        payment.DealTypeId = request.DealTypeId;
        payment.PaymentStatusId = request.PaymentStatusEntityId;
        payment.CounterpartyName = null;
        payment.SystemNotes = InvoiceLineItemStorage.RemoveFromSystemNotes(payment.SystemNotes);

        var responsibleIds = await ResolveInvoiceResponsibleIdsAsync(caseOwnerClientId, request.ResponsibleIds, ct);
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

        ApplyPaidFlags(payment, request.PaidDate);

        if (originalStatus != payment.Status)
        {
            PaymentDomainService.AppendSystemNote(
                payment,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {payment.Status.ToDisplayString()}",
                now,
                actor);
        }

        await SyncInvoiceLineItemsAsync(payment, request.LineItems, ct);
        await SyncInvoiceClientCasesAsync(payment, clientCaseValidation.ClientCaseIds, ct);
        await SyncInvoiceActsAsync(payment, actValidation.ActIds, number, ct, previousInvoiceNumber);
        var installmentSyncError = await SyncInstallmentPaymentsAsync(payment, request, responsibleIds, ct);
        if (installmentSyncError is not null)
        {
            return installmentSyncError;
        }
        await _db.SaveChangesAsync(ct);

        var dto = await LoadInvoiceDtoAsync(payment.Id, ct);

        return Ok(dto);
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

        var targetDate = (date ?? DateTime.UtcNow).Date;
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
        if (!User.IsInRole("admin"))
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
        if (!User.IsInRole("admin"))
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
        await UpsertSystemSettingAsync(InvoiceDocumentTemplatesSettingKey, InvoiceDocumentTemplateEngine.Serialize(storage), now, ct);
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
        if (!User.IsInRole("admin"))
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
        if (!User.IsInRole("admin"))
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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var payment = await _db.Payments
            .Include(p => p.Invoice)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var invoiceIdsToDelete = GetInvoiceEntityIdsToDelete(new[] { payment });
        var paymentsToDelete = await ExpandInvoiceAggregatePaymentsAsync(new[] { payment }, ct);
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

        var paymentsQuery = BuildEditableInvoicePaymentsQuery();
        var effectiveResponsibleId = ResolveVisibleResponsibleId(null);
        if (!User.IsInRole("admin") && !effectiveResponsibleId.HasValue)
        {
            return Unauthorized();
        }

        if (effectiveResponsibleId.HasValue)
        {
            paymentsQuery = paymentsQuery.Where(payment => payment.Responsibles.Any(row => row.UserId == effectiveResponsibleId.Value));
        }

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

        var paymentsQuery = BuildEditableInvoicePaymentsQuery();
        var effectiveResponsibleId = ResolveVisibleResponsibleId(null);
        if (!User.IsInRole("admin") && !effectiveResponsibleId.HasValue)
        {
            return Unauthorized();
        }

        if (effectiveResponsibleId.HasValue)
        {
            paymentsQuery = paymentsQuery.Where(payment => payment.Responsibles.Any(row => row.UserId == effectiveResponsibleId.Value));
        }

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
        var payment = await LoadInvoiceDocumentPaymentAsync(id, ct);

        if (payment is null)
        {
            return NotFound();
        }

        var normalizedFormat = NormalizeDocumentFormat(format);
        var lineItems = await LoadInvoiceLineItemsAsync(payment.Id, payment.SystemNotes, ct);
        var documentSettings = await GetInvoiceDocumentTemplateSettingsAsync(ct);
        var template = SelectInvoiceDocumentTemplate(documentSettings, templateKey);
        var mainCompany = await LoadMainCompanyAsync(ct);
        var shouldIncludeSignatureAndSeal = includeSignatureAndSeal ?? documentSettings.PrintWithSignatureAndSealByDefault;
        var installmentScheduleHtml = await BuildInstallmentScheduleHtmlAsync(payment, ct);
        var documentHtml = InvoiceDocumentTemplateEngine.RenderHtml(
            template.Html,
            payment,
            lineItems,
            mainCompany,
            shouldIncludeSignatureAndSeal,
            installmentScheduleHtml);

        if (normalizedFormat == "html")
        {
            return Content(documentHtml, "text/html; charset=utf-8");
        }

        var bytes = Encoding.UTF8.GetBytes(documentHtml);
        var ext = normalizedFormat == "pdf" ? "pdf" : "doc";
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

        return File(bytes, "application/msword", fileName);
    }

    [HttpPost("{id:int}/send-email")]
    public async Task<IActionResult> SendInvoiceEmail(int id, [FromBody] SendInvoiceEmailRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
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
        var documentSettings = await GetInvoiceDocumentTemplateSettingsAsync(ct);
        var template = SelectInvoiceDocumentTemplate(documentSettings, null);
        var mainCompany = await LoadMainCompanyAsync(ct);
        var installmentScheduleHtml = await BuildInstallmentScheduleHtmlAsync(payment, ct);
        var documentHtml = InvoiceDocumentTemplateEngine.RenderHtml(
            template.Html,
            payment,
            lineItems,
            mainCompany,
            documentSettings.PrintWithSignatureAndSealByDefault,
            installmentScheduleHtml);
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
        else
        {
            bytes = Encoding.UTF8.GetBytes(documentHtml);
            fileName = $"invoice-{SanitizeFilePart(payment.Invoice?.Number ?? payment.Account)}.doc";
            contentType = "application/msword";
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

    private IQueryable<InvoiceQueryItem> BuildInvoiceQuery()
    {
        var payments = BuildVisibleInvoicePaymentsQuery(trackChanges: false);

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

    private IQueryable<Payment> BuildEditableInvoicePaymentsQuery()
    {
        return BuildVisibleInvoicePaymentsQuery(trackChanges: true);
    }

    private IQueryable<Payment> BuildVisibleInvoicePaymentsQuery(bool trackChanges)
    {
        var payments = trackChanges
            ? _db.Payments
            : _db.Payments.AsNoTracking();

        return payments.Where(payment =>
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
        int? contractId,
        int? caseId,
        int? responsibleId,
        string? search,
        PaymentType? type,
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
            query = query.Where(x => x.Payment.ClientId == clientId.Value || x.Payment.ExpenseClientId == clientId.Value);
        }
        else if (clientIds is { Count: > 0 })
        {
            query = query.Where(x =>
                (x.Payment.ClientId.HasValue && clientIds.Contains(x.Payment.ClientId.Value))
                || (x.Payment.ExpenseClientId.HasValue && clientIds.Contains(x.Payment.ExpenseClientId.Value)));
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
            "client" => desc
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

    private static void ApplyPaidFlags(Payment payment, DateTime? paidDate)
    {
        var effectiveDate = (paidDate ?? payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;

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

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private async Task<ActionResult?> ValidateContractClientLinkAsync(int? contractId, int clientId, CancellationToken ct)
    {
        if (!contractId.HasValue) return null;

        var contractExists = await _db.Contracts.AsNoTracking().AnyAsync(c => c.Id == contractId.Value, ct);
        if (!contractExists) return BadRequest($"Unknown contractId {contractId.Value}");

        var linkExists = await _db.Set<ClientContract>().AsNoTracking()
            .AnyAsync(x => x.ContractId == contractId.Value && x.ClientId == clientId, ct);
        if (!linkExists) return BadRequest("Client is not linked to selected contract.");

        return null;
    }

    private async Task<ActionResult?> ValidateExpenseClientAsync(UpsertInvoiceRequest request, CancellationToken ct)
    {
        if (request.Type != PaymentType.Expense || !request.ExpenseClientId.HasValue)
        {
            return null;
        }

        var exists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ExpenseClientId.Value, ct);
        return exists ? null : BadRequest($"Unknown expenseClientId {request.ExpenseClientId.Value}");
    }

    private static int? ResolveInvoiceExpenseClientId(UpsertInvoiceRequest request)
        => request.Type == PaymentType.Expense && request.ExpenseClientId.HasValue && request.ExpenseClientId.Value > 0
            ? request.ExpenseClientId.Value
            : null;

    private static int ResolveInvoiceCaseOwnerClientId(UpsertInvoiceRequest request)
        => request.Type == PaymentType.Expense
            ? ResolveInvoiceExpenseClientId(request) ?? request.ClientId
            : request.ClientId;

    private async Task<(ActionResult? Error, List<int> ClientCaseIds)> ValidateInvoiceClientCasesAsync(int clientId, UpsertInvoiceRequest request, CancellationToken ct)
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
            .Select(x => new { x.Id, x.ClientId })
            .ToListAsync(ct);

        if (cases.Count != clientCaseIds.Count)
        {
            var existingIds = cases.Select(x => x.Id).ToHashSet();
            var missingIds = clientCaseIds.Where(id => !existingIds.Contains(id));
            return (BadRequest($"Unknown clientCaseIds: {string.Join(", ", missingIds)}"), new List<int>());
        }

        var foreignClientCaseIds = cases
            .Where(x => x.ClientId != clientId)
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToList();

        if (foreignClientCaseIds.Count > 0)
        {
            return (BadRequest($"Client cases are not linked to selected client: {string.Join(", ", foreignClientCaseIds)}"), new List<int>());
        }

        return (null, clientCaseIds);
    }

    private async Task<(ActionResult? Error, List<int> ActIds)> ValidateInvoiceActsAsync(int clientId, UpsertInvoiceRequest request, int? invoiceId, CancellationToken ct)
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
            .Select(x => new { x.Id, x.ClientId, x.InvoiceId })
            .ToListAsync(ct);

        if (acts.Count != actIds.Count)
        {
            var existingIds = acts.Select(x => x.Id).ToHashSet();
            var missingIds = actIds.Where(id => !existingIds.Contains(id));
            return (BadRequest($"Unknown actIds: {string.Join(", ", missingIds)}"), new List<int>());
        }

        var foreignClientActIds = acts
            .Where(x => x.ClientId != clientId)
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToList();

        if (foreignClientActIds.Count > 0)
        {
            return (BadRequest($"Acts are not linked to selected client: {string.Join(", ", foreignClientActIds)}"), new List<int>());
        }

        var linkedToOtherInvoices = acts
            .Where(x => x.InvoiceId.HasValue && x.InvoiceId.Value != invoiceId)
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToList();

        if (linkedToOtherInvoices.Count > 0)
        {
            return (BadRequest($"Acts are already linked to another invoice: {string.Join(", ", linkedToOtherInvoices)}"), new List<int>());
        }

        return (null, actIds);
    }

    private static readonly Expression<Func<InvoiceQueryItem, InvoiceProjectionRaw>> ProjectionRaw = x => new InvoiceProjectionRaw
    {
        Id = x.Payment.Id,
        InvoiceEntityId = x.Payment.InvoiceId,
        Number = x.Payment.Invoice != null ? x.Payment.Invoice.Number : (x.Payment.Account ?? string.Empty),
        Date = x.Payment.Invoice != null ? x.Payment.Invoice.Date : (x.Payment.AccountDate ?? x.Payment.Date),
        DueDate = x.Payment.Date,
        Amount = x.Payment.Amount,
        Type = x.Payment.Type,
        Status = x.Payment.Status,
        IsPaid = x.Payment.IsPaid,
        PaidDate = x.Payment.PaidDate,
        ClientId = x.Payment.ClientId,
        ExpenseClientId = x.Payment.ExpenseClientId,
        ContractId = x.Payment.ContractId,
        ClientName = x.Payment.Client != null ? x.Payment.Client.Name : null,
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
            }
            : null,
        ExpenseClientName = x.Payment.ExpenseClient != null ? x.Payment.ExpenseClient.Name : null,
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
            Date = raw.Date,
            DueDate = raw.DueDate,
            Amount = raw.Amount,
            Type = raw.Type,
            Status = raw.Status,
            IsPaid = raw.IsPaid,
            PaidDate = raw.PaidDate,
            ClientId = raw.ClientId,
            ExpenseClientId = raw.ExpenseClientId,
            ContractId = raw.ContractId,
            ClientName = raw.ClientName,
            ClientCompany = raw.ClientCompany,
            ClientStatusId = raw.ClientStatusId,
            ClientStatus = raw.ClientStatus,
            ExpenseClientName = raw.ExpenseClientName,
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
            ResponsibleId = raw.ResponsibleId,
            ResponsibleName = raw.ResponsibleName,
            CounterpartyInn = raw.CounterpartyInn,
            CounterpartyName = raw.CounterpartyName,
            ServiceId = raw.ServiceId,
            ServiceName = raw.ServiceName,
            PaymentStatusName = raw.PaymentStatusName,
            CreatedAt = raw.CreatedAt,
            AuditCreatedByUserId = raw.AuditCreatedByUserId,
            AuditCreatedByUserName = raw.AuditCreatedByUserName,
            AuditUpdatedAt = raw.AuditUpdatedAt,
            AuditUpdatedByUserId = raw.AuditUpdatedByUserId,
            AuditUpdatedByUserName = raw.AuditUpdatedByUserName,
            AuditIsDeleted = raw.AuditIsDeleted,
            AuditDeletedAt = raw.AuditDeletedAt,
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

    private async Task EnrichInvoiceDtosAsync(List<InvoiceDto> items, CancellationToken ct)
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

        var existingGeneratedPayments = await _db.Payments
            .Include(payment => payment.Responsibles)
            .Where(payment =>
                payment.InvoiceId == invoiceEntityId.Value
                && payment.Id != invoicePayment.Id
                && payment.DocumentKind != DocumentKind.Invoice)
            .ToListAsync(ct);

        var existingInstallmentPayments = existingGeneratedPayments
            .Select(payment => new
            {
                Payment = payment,
                Metadata = ExtractInstallmentPaymentMetadata(payment.SystemNotes),
            })
            .Where(item => item.Metadata is not null)
            .ToList();

        if (!ShouldReplaceInstallmentPlan(request))
        {
            return null;
        }

        if (request.InstallmentPlan is null || request.InstallmentPlan.Items.Count == 0)
        {
            foreach (var existing in existingInstallmentPayments)
            {
                if (HasProtectedInstallmentActivity(existing.Payment))
                {
                    return BadRequest("Нельзя удалить график рассрочки: по одному из платежей уже есть движение.");
                }
            }

            if (existingInstallmentPayments.Count > 0)
            {
                _db.Payments.RemoveRange(existingInstallmentPayments.Select(item => item.Payment));
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
                    responsibleIds);
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
                responsibleIds);

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

        return null;
    }

    private static bool ShouldReplaceInstallmentPlan(UpsertInvoiceRequest request)
    {
        return request.ReplaceInstallmentPlan || request.InstallmentPlan is not null;
    }

    private static DateTime ResolveInvoiceDueDate(UpsertInvoiceRequest request)
    {
        var installmentDueDate = request.InstallmentPlan?.Items?
            .Where(item => item is not null)
            .Select(item => (DateTime?)item.Date.Date)
            .Max();

        if (installmentDueDate.HasValue && installmentDueDate.Value != default)
        {
            return installmentDueDate.Value;
        }

        return (request.DueDate ?? request.Date).Date;
    }

    private void ApplyInstallmentPayment(
        Payment payment,
        Payment invoicePayment,
        UpsertInvoiceRequest request,
        InvoiceInstallmentPlanRequest plan,
        InvoiceInstallmentPlanItemRequest item,
        IReadOnlyCollection<int> responsibleIds)
    {
        var invoiceEntityId = invoicePayment.InvoiceId ?? invoicePayment.Invoice?.Id;
        payment.InvoiceId = invoiceEntityId;
        payment.Account = invoicePayment.Account;
        payment.AccountDate = invoicePayment.AccountDate;
        payment.DocumentKind = DocumentKind.Payment;
        payment.Date = item.Date.Date;
        payment.PlannedDate = item.Date.Date;
        payment.Amount = Math.Max(item.Payment, 0);
        payment.Type = invoicePayment.Type;
        payment.Status = PaymentStatus.Pending;
        payment.Description = BuildInstallmentDescription(invoicePayment.Account, item.Sequence, plan.Items.Count);
        payment.Notes = BuildInstallmentNotes(item);
        payment.ClientId = request.ClientId;
        payment.ExpenseClientId = invoicePayment.ExpenseClientId;
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

        if (!HasProtectedInstallmentActivity(payment))
        {
            payment.PaidAmount = 0m;
            payment.PaidDate = null;
            payment.LastPaymentDate = null;
            payment.IsPaid = false;
        }

        SyncPaymentResponsibles(payment, responsibleIds);
        PaymentDomainService.Normalize(payment);
        PaymentDomainService.ApplyStatusRules(payment, DateTime.UtcNow);
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
        var sameExpenseClient = payment.ExpenseClientId == invoicePayment.ExpenseClientId;
        var sameCase = payment.ClientCaseId == invoicePayment.ClientCaseId;
        var sameContract = payment.ContractId == request.ContractId;
        return sameDate && sameAmount && sameClient && sameExpenseClient && sameCase && sameContract;
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
        return string.Join(
            "\n",
            new[]
            {
                $"Основной долг: {item.Principal:0.00}",
                $"Проценты: {item.Interest:0.00}",
                $"Остаток: {item.Balance:0.00}",
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
            return JsonSerializer.Deserialize<InstallmentPaymentMetadataDto>(json);
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

        foreach (var act in currentActs.Where(a => !targetIds.Contains(a.Id)))
        {
            act.InvoiceId = null;
            if (!string.IsNullOrWhiteSpace(act.InvoiceNumber) && invoiceNumbers.Contains(act.InvoiceNumber))
            {
                act.InvoiceNumber = null;
            }
            act.UpdatedAt = DateTime.UtcNow;
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
            act.UpdatedAt = DateTime.UtcNow;
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

    private async Task<HashSet<int>> ResolveInvoiceResponsibleIdsAsync(int clientId, List<int>? responsibleIds, CancellationToken ct)
    {
        var normalized = NormalizeResponsibleIds(responsibleIds);
        if (normalized.Count > 0)
        {
            return normalized;
        }

        var clientResponsibleUserId = await _db.Clients
            .AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => c.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
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
        var duplicateError = await EnsureInvoiceNumberIsUniqueAsync(number, accountDate, excludeId, ct);
        if (duplicateError is null)
        {
            return number;
        }

        if (!allowDuplicateOnImport)
        {
            return null;
        }

        var baseNumber = number;
        for (var i = 1; i <= 9999; i++)
        {
            var suffix = $"-IMP{i}";
            var maxBaseLength = Math.Max(1, 120 - suffix.Length);
            var truncatedBase = baseNumber.Length > maxBaseLength
                ? baseNumber[..maxBaseLength]
                : baseNumber;
            var candidate = $"{truncatedBase}{suffix}";
            var candidateError = await EnsureInvoiceNumberIsUniqueAsync(candidate, accountDate, excludeId, ct);
            if (candidateError is null)
            {
                return candidate;
            }
        }

        return null;
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

    private int? ResolveVisibleResponsibleId(int? requestedResponsibleId)
    {
        if (User.IsInRole("admin"))
        {
            return requestedResponsibleId;
        }

        return GetUserId(User);
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
        return await _db.Payments
            .AsNoTracking()
            .Include(p => p.Invoice)
            .Include(p => p.Client)
                .ThenInclude(client => client!.Addresses)
            .Include(p => p.Client)
                .ThenInclude(client => client!.Passports)
            .Include(p => p.ExpenseClient)
                .ThenInclude(client => client!.Addresses)
            .Include(p => p.ExpenseClient)
                .ThenInclude(client => client!.Passports)
            .Include(p => p.Contract)
            .Include(p => p.ClientCase)
            .Include(p => p.InvoiceClientCases)
                .ThenInclude(link => link.ClientCase)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    private async Task<string> BuildInstallmentScheduleHtmlAsync(Payment payment, CancellationToken ct)
    {
        var invoiceId = payment.InvoiceId ?? payment.Invoice?.Id;
        if (!invoiceId.HasValue)
        {
            return string.Empty;
        }

        var installments = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.InvoiceId == invoiceId.Value
                && p.Id != payment.Id
                && p.SystemNotes.Contains(InstallmentPaymentMarker))
            .Select(p => new { p.Id, p.Date, p.Amount, p.SystemNotes })
            .ToListAsync(ct);

        if (installments.Count == 0)
        {
            return string.Empty;
        }

        var items = new List<InstallmentScheduleItem>(installments.Count);
        foreach (var installment in installments)
        {
            var metadata = ExtractInstallmentPaymentMetadata(installment.SystemNotes);
            var metadataItem = metadata?.Item;
            var date = metadataItem is not null && metadataItem.Date != default ? metadataItem.Date : installment.Date;
            var amount = metadataItem is not null && metadataItem.Payment > 0m ? metadataItem.Payment : installment.Amount;
            var sequence = metadataItem?.Sequence ?? 0;
            items.Add(new InstallmentScheduleItem(sequence, date, amount, installment.Id));
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
        public PaymentType Type { get; init; }
        public PaymentStatus Status { get; init; }
        public bool IsPaid { get; init; }
        public DateTime? PaidDate { get; init; }
        public int? ClientId { get; init; }
        public int? ExpenseClientId { get; init; }
        public int? ContractId { get; init; }
        public string? ClientName { get; init; }
        public string? ClientCompany { get; init; }
        public int? ClientStatusId { get; init; }
        public ClientStatusDto? ClientStatus { get; init; }
        public string? ExpenseClientName { get; init; }
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
