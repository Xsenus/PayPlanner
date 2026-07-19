using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text;
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
using PayPlanner.Api.Services.Contracts;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ContractsController : ControllerBase
{
    private const string ContractNumberStartSettingKey = "contracts.numbering.startNumber";
    private const string ContractNumberStrategySettingKey = "contracts.numbering.strategy";
    private const string ContractNumberTemplateSettingKey = "contracts.numbering.template";
    private const string ContractAutoGenerateNumberSettingKey = "contracts.rules.autoGenerateWhenEmpty";
    private const string ContractAllowManualNumberEditSettingKey = "contracts.rules.allowManualNumberEdit";
    private const string ContractDocumentTemplatesSettingKey = "contracts.documentTemplates";
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string DefaultMainCompanyIdSettingKey = "legalEntities.defaultMainCompanyId";
    private const string DefaultContractNumberTemplate = "{N}-{DD}/{MM}-{YY}";
    private const string ContractTerminationClientEmailsEnabledKey = "notifications.email.contractTerminationClientEnabled";

    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;
    private readonly IInvoiceDocumentPdfService? _invoiceDocumentPdfService;
    private readonly IEmailService? _emailService;
    private readonly INotificationService? _notificationService;
    private readonly IYandexDiskService? _yandexDiskService;

    public ContractsController(
        PaymentContext db,
        IFormRequirementsService formRequirements,
        IInvoiceDocumentPdfService? invoiceDocumentPdfService = null,
        IEmailService? emailService = null,
        INotificationService? notificationService = null,
        IYandexDiskService? yandexDiskService = null)
    {
        _db = db;
        _formRequirements = formRequirements;
        _invoiceDocumentPdfService = invoiceDocumentPdfService;
        _emailService = emailService;
        _notificationService = notificationService;
        _yandexDiskService = yandexDiskService;
    }

    private Task<DataVisibilityAccess?> ResolveContractsVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Contracts, ct);

    private ActionResult MissingContractsVisibilityResult()
        => User.GetUserId().HasValue ? Forbid() : Unauthorized();

    private async Task<bool> IsDatabaseAdminAsync(CancellationToken ct)
        => await _db.IsUserAdminOrLegacyFallbackAsync(User, ct);

    private Task<TimeZoneInfo> ResolveContractTimeZoneAsync(CancellationToken ct)
        => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);

    private async Task<IReadOnlyCollection<int>> ResolveContractNotificationRecipientsAsync(int contractId, CancellationToken ct)
    {
        var clientCandidates = _db.ClientContracts
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Client.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Client.ResponsibleUserId!.Value });
        var legalEntityCandidates = _db.LegalEntityContracts
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.LegalEntity.ResponsibleUserId!.Value });
        var invoiceResponsibleCandidates = _db.ContractInvoices
            .AsNoTracking()
            .Where(link => link.ContractId == contractId)
            .SelectMany(link => link.Invoice.Responsibles.Select(responsible => new { responsible.UserId }));
        var invoiceClientCandidates = _db.ContractInvoices
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Invoice.Client != null
                && link.Invoice.Client.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Invoice.Client!.ResponsibleUserId!.Value });
        var invoiceLegalEntityCandidates = _db.ContractInvoices
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Invoice.LegalEntity != null
                && link.Invoice.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Invoice.LegalEntity!.ResponsibleUserId!.Value });
        var linkedPaymentResponsibleCandidates = _db.ContractPayments
            .AsNoTracking()
            .Where(link => link.ContractId == contractId)
            .SelectMany(link => link.Payment.Responsibles.Select(responsible => new { responsible.UserId }));
        var linkedPaymentClientCandidates = _db.ContractPayments
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Payment.Client != null
                && link.Payment.Client.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Payment.Client!.ResponsibleUserId!.Value });
        var linkedPaymentLegalEntityCandidates = _db.ContractPayments
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Payment.LegalEntity != null
                && link.Payment.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Payment.LegalEntity!.ResponsibleUserId!.Value });
        var actResponsibleCandidates = _db.ContractActs
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Act.ResponsibleId.HasValue)
            .Select(link => new { UserId = link.Act.ResponsibleId!.Value });
        var actClientCandidates = _db.ContractActs
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Act.Client != null
                && link.Act.Client.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Act.Client!.ResponsibleUserId!.Value });
        var actLegalEntityCandidates = _db.ContractActs
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Act.LegalEntity != null
                && link.Act.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => new { UserId = link.Act.LegalEntity!.ResponsibleUserId!.Value });
        var directPaymentResponsibleCandidates = _db.Payments
            .AsNoTracking()
            .Where(payment => payment.ContractId == contractId)
            .SelectMany(payment => payment.Responsibles.Select(responsible => new { responsible.UserId }));
        var directPaymentClientCandidates = _db.Payments
            .AsNoTracking()
            .Where(payment => payment.ContractId == contractId
                && payment.Client != null
                && payment.Client.ResponsibleUserId.HasValue)
            .Select(payment => new { UserId = payment.Client!.ResponsibleUserId!.Value });
        var directPaymentLegalEntityCandidates = _db.Payments
            .AsNoTracking()
            .Where(payment => payment.ContractId == contractId
                && payment.LegalEntity != null
                && payment.LegalEntity.ResponsibleUserId.HasValue)
            .Select(payment => new { UserId = payment.LegalEntity!.ResponsibleUserId!.Value });

        var userIds = (await clientCandidates
                .Concat(legalEntityCandidates)
                .Concat(invoiceResponsibleCandidates)
                .Concat(invoiceClientCandidates)
                .Concat(invoiceLegalEntityCandidates)
                .Concat(linkedPaymentResponsibleCandidates)
                .Concat(linkedPaymentClientCandidates)
                .Concat(linkedPaymentLegalEntityCandidates)
                .Concat(actResponsibleCandidates)
                .Concat(actClientCandidates)
                .Concat(actLegalEntityCandidates)
                .Concat(directPaymentResponsibleCandidates)
                .Concat(directPaymentClientCandidates)
                .Concat(directPaymentLegalEntityCandidates)
                .Where(candidate => candidate.UserId > 0)
                .Select(candidate => candidate.UserId)
                .Distinct()
                .ToListAsync(ct))
            .ToArray();
        if (userIds.Length == 0)
        {
            return Array.Empty<int>();
        }

        var allowedUserIds = await _db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Contracts
                        && permission.CanView)))
            .Select(user => user.Id)
            .ToListAsync(ct);

        var result = new List<int>();
        foreach (var userId in allowedUserIds)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)) },
                authenticationType: "ContractNotificationAccessCheck"));
            var visibility = await _db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Contracts, ct);
            if (visibility is null)
            {
                continue;
            }

            var canOpenContract = await _db.Contracts
                .AsNoTracking()
                .Where(contract => contract.Id == contractId)
                .ApplyContractVisibility(visibility)
                .AnyAsync(ct);
            if (canOpenContract)
            {
                result.Add(userId);
            }
        }

        return result;
    }

    private async Task NotifyContractCreatedAsync(Contract contract, CancellationToken ct)
    {
        if (_notificationService is null)
        {
            return;
        }

        var recipients = await ResolveContractNotificationRecipientsAsync(contract.Id, ct);
        if (recipients.Count == 0)
        {
            return;
        }

        await _notificationService.NotifyContractCreatedAsync(
            contract,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipients,
            ct);
    }

    private async Task NotifyContractChangedAsync(Contract contract, IReadOnlyList<string> changeLabels, CancellationToken ct)
    {
        if (_notificationService is null || changeLabels.Count == 0)
        {
            return;
        }

        var recipients = await ResolveContractNotificationRecipientsAsync(contract.Id, ct);
        if (recipients.Count == 0)
        {
            return;
        }

        await _notificationService.NotifyContractChangedAsync(
            contract,
            changeLabels,
            User.GetDisplayName(),
            User.GetUserId() ?? 0,
            recipients,
            ct);
    }

    private static IReadOnlyList<string> BuildContractChangeLabels(
        string? originalStatus,
        string? currentStatus,
        DateTime? originalValidUntil,
        DateTime? currentValidUntil,
        decimal? originalAmount,
        decimal? currentAmount)
    {
        var labels = new List<string>();
        var normalizedOriginalStatus = ContractStatus.Normalize(originalStatus) ?? originalStatus;
        var normalizedCurrentStatus = ContractStatus.Normalize(currentStatus) ?? currentStatus;
        if (!string.Equals(normalizedOriginalStatus, normalizedCurrentStatus, StringComparison.Ordinal))
        {
            labels.Add($"Статус договора: {FormatContractStatus(normalizedOriginalStatus)} -> {FormatContractStatus(normalizedCurrentStatus)}");
        }

        if (originalValidUntil?.Date != currentValidUntil?.Date)
        {
            labels.Add($"Срок действия: {FormatContractDate(originalValidUntil)} -> {FormatContractDate(currentValidUntil)}");
        }

        if (originalAmount != currentAmount)
        {
            labels.Add($"Сумма: {FormatContractAmount(originalAmount)} -> {FormatContractAmount(currentAmount)}");
        }

        return labels;
    }

    private static string FormatContractDate(DateTime? date)
        => date.HasValue ? date.Value.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")) : "не указана";

    private static string FormatContractAmount(decimal? amount)
        => amount.HasValue ? amount.Value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) : "не указана";

    private static string FormatContractStatus(string? status)
        => (ContractStatus.Normalize(status) ?? status)?.Trim() switch
        {
            ContractStatus.Draft => "Черновик",
            ContractStatus.NotSigned => "Не подписан",
            ContractStatus.RequiresClientSignature => "Требует подписи клиента",
            ContractStatus.OnApproval => "На согласовании",
            ContractStatus.Approved => "Согласован",
            ContractStatus.ApprovalRejected => "Согласование отклонено",
            ContractStatus.NeedsCorrection => "Требует корректировки",
            ContractStatus.OnSigning => "Отправлен на подпись",
            ContractStatus.Concluded => "Заключен",
            ContractStatus.Terminated => "Расторгнут",
            ContractStatus.Cancelled => "Аннулирован",
            _ => "не указан"
        };

    private async Task<Contract?> LoadEditableContractAsync(int id, CancellationToken ct)
    {
        var visibility = await ResolveContractsVisibilityAsync(ct);
        if (visibility is null)
        {
            return null;
        }

        return await _db.Contracts
            .ApplyContractVisibility(visibility)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    private async Task<Contract?> LoadEditableContractDetailAsync(int id, CancellationToken ct)
    {
        var visibility = await ResolveContractsVisibilityAsync(ct);
        if (visibility is null)
        {
            return null;
        }

        return await BuildContractDetailQuery(visibility)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId,
        [FromQuery] string? search, [FromQuery] string? sortBy, [FromQuery] string? sortDir, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default, [FromQuery] string? status = null,
        [FromQuery] int? invoiceId = null, [FromQuery] int? actId = null, [FromQuery] int? paymentId = null,
        [FromQuery] int? legalEntityId = null)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        var visibility = await ResolveContractsVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingContractsVisibilityResult();
        }

        var normalizedStatus = NormalizeContractStatusOrNull(status);
        if (!string.IsNullOrWhiteSpace(status) && normalizedStatus is null)
        {
            return ValidationError(nameof(status), "Unknown contract status.");
        }

        var query = ApplySort(ApplyFilters(BuildContractsListCountQuery(visibility), from, to, clientId, legalEntityId, invoiceId, actId, paymentId, normalizedStatus, search), sortBy, sortDir);
        var total = await query.CountAsync(ct);
        var contracts = await ApplySort(ApplyFilters(BuildContractDetailQuery(visibility), from, to, clientId, legalEntityId, invoiceId, actId, paymentId, normalizedStatus, search), sortBy, sortDir)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        var items = contracts.Select(MapToDto).ToList();
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ContractDto>> GetById(int id, CancellationToken ct)
    {
        var visibility = await ResolveContractsVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingContractsVisibilityResult();
        }

        var contract = await BuildContractDetailQuery(visibility).FirstOrDefaultAsync(c => c.Id == id, ct);
        var dto = contract is null ? null : MapToDto(contract);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpGet("{id:int}/document")]
    public async Task<IActionResult> GetDocument(
        int id,
        [FromQuery] string? format = "word",
        [FromQuery] string? templateKey = null,
        CancellationToken ct = default)
    {
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var contract = await LoadContractForDocumentAsync(id, ct);
        if (contract is null) return NotFound();

        var mainCompany = await LoadMainCompanyAsync(ct);
        var template = await GetContractDocumentTemplateAsync(templateKey, ct);
        var documentHtml = ContractDocumentTemplateEngine.RenderHtml(template.Html, contract, mainCompany);
        var normalizedFormat = NormalizeDocumentFormat(format);

        if (normalizedFormat == "html")
        {
            return Content(documentHtml, "text/html; charset=utf-8");
        }

        var ext = normalizedFormat == "pdf" ? "pdf" : "doc";
        var fileName = $"contract-{SanitizeFilePart(contract.Number)}.{ext}";

        if (normalizedFormat == "pdf")
        {
            try
            {
                var pdfBytes = await RenderContractPdfAsync(documentHtml, ct);
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        return File(Encoding.UTF8.GetBytes(documentHtml), "application/msword", fileName);
    }

    [HttpGet("{id:int}/termination-agreement")]
    public async Task<IActionResult> GetTerminationAgreement(int id, [FromQuery] string? format = "word", CancellationToken ct = default)
    {
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var contract = await LoadContractForDocumentAsync(id, ct);
        if (contract is null) return NotFound();
        if (!string.Equals(contract.Status, ContractStatus.Terminated, StringComparison.OrdinalIgnoreCase)
            || contract.TerminationDate is null
            || string.IsNullOrWhiteSpace(contract.TerminationTerms))
        {
            return BadRequest("Соглашение о расторжении доступно после расторжения договора с заполненными условиями.");
        }

        var mainCompany = await LoadMainCompanyAsync(ct);
        var documentHtml = ContractDocumentTemplateEngine.RenderTerminationAgreementHtml(contract, mainCompany);
        var normalizedFormat = NormalizeDocumentFormat(format);

        if (normalizedFormat == "html")
        {
            return Content(documentHtml, "text/html; charset=utf-8");
        }

        var ext = normalizedFormat == "pdf" ? "pdf" : "doc";
        var fileName = $"contract-termination-{SanitizeFilePart(contract.Number)}.{ext}";

        if (normalizedFormat == "pdf")
        {
            try
            {
                var pdfBytes = await RenderContractPdfAsync(documentHtml, ct);
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        return File(Encoding.UTF8.GetBytes(documentHtml), "application/msword", fileName);
    }

    [HttpPost("{id:int}/yandex-disk")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> UploadDocumentToYandexDisk(
        int id,
        [FromQuery] string? format = "pdf",
        [FromQuery] string? templateKey = null,
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

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanExport, ct) is { } contractForbidden)
        {
            return contractForbidden;
        }

        var contract = await LoadContractForDocumentAsync(id, ct);
        if (contract is null) return NotFound();

        var mainCompany = await LoadMainCompanyAsync(ct);
        var template = await GetContractDocumentTemplateAsync(templateKey, ct);
        var documentHtml = ContractDocumentTemplateEngine.RenderHtml(template.Html, contract, mainCompany);
        var normalizedFormat = NormalizeDocumentFormat(format);
        if (normalizedFormat == "html")
        {
            normalizedFormat = "word";
        }

        byte[] bytes;
        string fileName;
        string contentType;
        if (normalizedFormat == "pdf")
        {
            try
            {
                bytes = await RenderContractPdfAsync(documentHtml, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }

            fileName = $"contract-{SanitizeFilePart(contract.Number)}.pdf";
            contentType = "application/pdf";
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(documentHtml);
            fileName = $"contract-{SanitizeFilePart(contract.Number)}.doc";
            contentType = "application/msword";
        }

        await using var stream = new MemoryStream(bytes);
        try
        {
            var result = await _yandexDiskService.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
                stream,
                contentType,
                fileName,
                "contract",
                "Договор",
                contract.Number,
                contract.Date,
                contract.Title ?? $"Договор {contract.Number}",
                contract.ClientContracts.FirstOrDefault()?.Client?.Name ?? contract.Clients.FirstOrDefault()?.Name,
                contract.LegalEntityContracts.FirstOrDefault()?.LegalEntity?.ShortName ?? contract.LegalEntities.FirstOrDefault()?.ShortName,
                null,
                contract.Title,
                overwrite),
                ct);

            await YandexDiskCrmSyncStateRecorder.RecordSuccessAsync(
                _db,
                "contract",
                "contract",
                contract.Id,
                fileName,
                result.Path,
                null,
                ct);

            _db.ObjectAuditEvents.Add(new ObjectAuditEvent
            {
                ObjectType = "contract",
                ObjectId = contract.Id,
                Action = "YandexDiskUploaded",
                Description = $"Договор «{contract.Number}» отправлен на Яндекс.Диск.",
                ActorUserId = User.GetUserId(),
                ActorUserName = User.GetDisplayName(),
                MetadataJson = JsonSerializer.Serialize(new { contract.Id, contract.Number, FileName = fileName, result.Path }),
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch (YandexDiskOperationException ex)
        {
            await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
                _db,
                "contract",
                "contract",
                contract.Id,
                fileName,
                null,
                ex,
                ct);
            await _db.SaveChangesAsync(ct);
            return MapYandexDiskError(ex);
        }
    }

    [HttpPost("{id:int}/send-email")]
    public async Task<IActionResult> SendEmail(int id, [FromBody] SendInvoiceEmailRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (_emailService is null)
        {
            return BadRequest("Email service is not configured.");
        }

        var contract = await LoadContractForDocumentAsync(id, ct);
        if (contract is null) return NotFound();

        var email = string.IsNullOrWhiteSpace(request.Email)
            ? ResolveContractRecipientEmail(contract)
            : request.Email.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Recipient email is required.");
        }

        var mainCompany = await LoadMainCompanyAsync(ct);
        var template = await GetContractDocumentTemplateAsync(preferredTemplateKey: null, ct);
        var documentHtml = ContractDocumentTemplateEngine.RenderHtml(template.Html, contract, mainCompany);
        var normalizedFormat = NormalizeDocumentFormat(request.Format);
        if (normalizedFormat == "html")
        {
            normalizedFormat = "word";
        }

        byte[] bytes;
        string fileName;
        string contentType;

        if (normalizedFormat == "pdf")
        {
            try
            {
                bytes = await RenderContractPdfAsync(documentHtml, ct);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }

            fileName = $"contract-{SanitizeFilePart(contract.Number)}.pdf";
            contentType = "application/pdf";
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(documentHtml);
            fileName = $"contract-{SanitizeFilePart(contract.Number)}.doc";
            contentType = "application/msword";
        }

        await _emailService.SendEmailWithAttachmentAsync(
            email,
            string.IsNullOrWhiteSpace(request.Subject) ? $"Договор № {contract.Number}" : request.Subject.Trim(),
            string.IsNullOrWhiteSpace(request.Message) ? "Во вложении договор." : request.Message.Trim(),
            fileName,
            bytes,
            contentType);

        return Ok(new { sent = true, recipient = email, fileName, format = normalizedFormat });
    }

    [HttpGet("document-template-settings")]
    public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> GetDocumentTemplateSettings(CancellationToken ct)
        => Ok(await GetContractDocumentTemplateSettingsAsync(ct));

    [HttpPut("document-template-settings")]
    public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> UpsertDocumentTemplateSettings(
        [FromBody] InvoiceDocumentTemplateSettingsRequest request,
        CancellationToken ct)
    {
        if (!await IsDatabaseAdminAsync(ct))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var storage = ContractDocumentTemplateEngine.NormalizeRequest(request);
        var validationError = ValidateContractDocumentTemplateSettings(storage);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var now = DateTime.UtcNow;
        await UpsertSystemSettingAsync(ContractDocumentTemplatesSettingKey, ContractDocumentTemplateEngine.Serialize(storage), now, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(ContractDocumentTemplateEngine.CreateResponse(storage));
    }

    [HttpPost("{id:int}/send-to-signing")]
    public async Task<ActionResult<ContractDto>> SendToSigning(int id, CancellationToken ct)
    {
        var contract = await LoadEditableContractAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        contract.Status = ContractStatus.OnSigning;
        contract.SentForSigningAt ??= DateTime.UtcNow;
        contract.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractChangedAsync(
            updatedContract,
            BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
            ct);
        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpGet("{id:int}/termination-check")]
    public async Task<ActionResult<ContractTerminationCheckDto>> GetTerminationCheck(int id, CancellationToken ct)
    {
        var contract = await LoadContractForDocumentAsync(id, ct);
        if (contract is null) return NotFound();

        return Ok(BuildTerminationCheck(contract));
    }

    [HttpPost("{id:int}/mark-concluded")]
    public async Task<ActionResult<ContractDto>> MarkConcluded(int id, CancellationToken ct)
    {
        var contract = await LoadEditableContractAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        var now = DateTime.UtcNow;
        contract.Status = ContractStatus.Concluded;
        contract.SentForSigningAt ??= now;
        contract.ConcludedAt ??= now;
        contract.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractChangedAsync(
            updatedContract,
            BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
            ct);
        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpPost("{id:int}/mark-not-signed")]
    public async Task<ActionResult<ContractDto>> MarkNotSigned(int id, CancellationToken ct)
    {
        var contract = await LoadEditableContractAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        contract.Status = ContractStatus.NotSigned;
        contract.SentForSigningAt = null;
        contract.ConcludedAt = null;
        contract.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractChangedAsync(
            updatedContract,
            BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
            ct);
        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpPost("{id:int}/status")]
    public async Task<ActionResult<ContractDto>> UpdateStatus(int id, [FromBody] UpdateNamedStatusRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var status = NormalizeContractStatusOrNull(request.Status);
        if (status is null)
        {
            return ValidationError(nameof(request.Status), "Unknown contract status.");
        }

        var contract = await LoadEditableContractDetailAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        if (string.Equals(status, ContractStatus.Terminated, StringComparison.OrdinalIgnoreCase))
        {
            var terminationCheck = BuildTerminationCheck(contract);
            if (terminationCheck.HasUnpaidInvoices)
            {
                return BadRequest($"По договору есть неоплаченные счета: {terminationCheck.UnpaidInvoiceCount} шт. на сумму {terminationCheck.UnpaidInvoiceAmount:N2}. Используйте действие «Расторгнуть» и заполните финансовые условия.");
            }
        }

        var reason = Normalize(request.Reason);
        var statusChanged = !string.Equals(contract.Status, status, StringComparison.OrdinalIgnoreCase);
        if (statusChanged)
        {
            var reasonError = await ValidateFinancialActionReasonAsync(reason, "изменения статуса договора", ct);
            if (reasonError is not null)
            {
                return reasonError;
            }

            ApplyAuditReason(reason);
        }

        var now = DateTime.UtcNow;
        contract.Status = status;
        contract.UpdatedAt = now;

        switch (status)
        {
            case ContractStatus.Draft:
            case ContractStatus.NotSigned:
            case ContractStatus.RequiresClientSignature:
            case ContractStatus.OnApproval:
            case ContractStatus.Approved:
            case ContractStatus.ApprovalRejected:
            case ContractStatus.NeedsCorrection:
                contract.SentForSigningAt = null;
                contract.ConcludedAt = null;
                contract.TerminationDate = null;
                contract.TerminationReason = null;
                contract.TerminationTerms = null;
                contract.TerminationFinancialTerms = null;
                contract.CancelledAt = null;
                contract.CancelledReason = null;
                break;
            case ContractStatus.OnSigning:
                contract.SentForSigningAt ??= now;
                contract.ConcludedAt = null;
                contract.TerminationDate = null;
                contract.TerminationReason = null;
                contract.TerminationTerms = null;
                contract.TerminationFinancialTerms = null;
                contract.CancelledAt = null;
                contract.CancelledReason = null;
                break;
            case ContractStatus.Concluded:
                contract.SentForSigningAt ??= now;
                contract.ConcludedAt ??= now;
                contract.TerminationDate = null;
                contract.TerminationReason = null;
                contract.TerminationTerms = null;
                contract.TerminationFinancialTerms = null;
                contract.CancelledAt = null;
                contract.CancelledReason = null;
                break;
            case ContractStatus.Terminated:
                contract.TerminationDate ??= ContractDomainService.NormalizeLocalDate(now);
                contract.TerminationTerms ??= "Быстрая смена статуса";
                contract.CancelledAt = null;
                contract.CancelledReason = null;
                break;
            case ContractStatus.Cancelled:
                contract.CancelledAt ??= ContractDomainService.NormalizeLocalDate(now);
                contract.CancelledReason ??= reason ?? "Быстрая смена статуса";
                contract.TerminationDate = null;
                contract.TerminationReason = null;
                contract.TerminationTerms = null;
                contract.TerminationFinancialTerms = null;
                break;
        }

        await _db.SaveChangesAsync(ct);

        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        if (statusChanged)
        {
            await NotifyContractChangedAsync(
                updatedContract,
                BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
                ct);
        }

        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpPost("{id:int}/terminate")]
    public async Task<ActionResult<ContractDto>> Terminate(int id, [FromBody] TerminateContractRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (request.TerminationDate == default) return ValidationError(nameof(request.TerminationDate), "Дата расторжения обязательна.");

        var terms = Normalize(request.Terms);
        if (terms is null) return ValidationError(nameof(request.Terms), "Условия расторжения обязательны.");
        var terminationReason = Normalize(request.Reason);
        var reasonError = await ValidateFinancialActionReasonAsync(terminationReason, "расторжения договора", ct);
        if (reasonError is not null)
        {
            return reasonError;
        }

        var contract = await LoadEditableContractDetailAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }
        if (string.Equals(contract.Status, ContractStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Аннулированный договор нельзя расторгнуть.");
        }

        var terminationCheck = BuildTerminationCheck(contract);
        if (terminationCheck.HasUnpaidInvoices && string.IsNullOrWhiteSpace(request.FinancialTerms))
        {
            return BadRequest($"По договору есть неоплаченные счета: {terminationCheck.UnpaidInvoiceCount} шт. на сумму {terminationCheck.UnpaidInvoiceAmount:N2}. Заполните финансовые условия расторжения.");
        }

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        contract.Status = ContractStatus.Terminated;
        contract.TerminationDate = ContractDomainService.NormalizeLocalDate(request.TerminationDate);
        contract.TerminationReason = terminationReason;
        contract.TerminationTerms = terms;
        contract.TerminationFinancialTerms = Normalize(request.FinancialTerms);
        contract.UpdatedAt = DateTime.UtcNow;
        ApplyAuditReason(terminationReason);

        await _db.SaveChangesAsync(ct);
        await SendContractTerminationNotificationAsync(contract, ct);

        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractChangedAsync(
            updatedContract,
            BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
            ct);
        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<ContractDto>> Cancel(int id, [FromBody] CancelContractRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var reason = Normalize(request.Reason);
        if (reason is null) return ValidationError(nameof(request.Reason), "Причина аннулирования обязательна.");
        ApplyAuditReason(reason);

        var contract = await LoadEditableContractDetailAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }
        if (!string.Equals(contract.Status, ContractStatus.Draft, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.NotSigned, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.RequiresClientSignature, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.OnApproval, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.Approved, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.ApprovalRejected, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.NeedsCorrection, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contract.Status, ContractStatus.OnSigning, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Аннулирование доступно только для черновиков и договоров на подписании.");
        }

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        var now = DateTime.UtcNow;
        var contractTimeZone = await ResolveContractTimeZoneAsync(ct);
        contract.Status = ContractStatus.Cancelled;
        contract.CancelledAt ??= ContractDomainService.NormalizeLocalDate(now);
        contract.CancelledReason = reason;
        contract.UpdatedAt = now;
        ApplyContractCancellationSideEffects(contract, reason, now, contractTimeZone);

        await _db.SaveChangesAsync(ct);

        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractChangedAsync(
            updatedContract,
            BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
            ct);
        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpGet("numbering-settings")]
    public async Task<ActionResult<ContractNumberingSettingsDto>> GetNumberingSettings(CancellationToken ct)
        => Ok(await GetContractNumberingSettingsAsync(ct));

    [HttpPut("numbering-settings")]
    public async Task<ActionResult<ContractNumberingSettingsDto>> UpsertNumberingSettings([FromBody] ContractNumberingSettingsRequest request, CancellationToken ct)
    {
        if (!await IsDatabaseAdminAsync(ct)) return Forbid();
        if (request.StartNumber < 1) return BadRequest("Стартовый номер договора должен быть больше 0.");
        var template = NormalizeContractTemplate(request.Template);
        if (!TryExtractSequenceToken(template, out _)) return BadRequest("Шаблон номера договора должен содержать один маркер номера: {N}, {NN}, {NNN} или {NNNN}.");
        var allocationStrategy = ParseContractAllocationStrategy(request.AllocationStrategy);
        var now = DateTime.UtcNow;
        await UpsertSystemSettingAsync(ContractNumberStartSettingKey, request.StartNumber.ToString(CultureInfo.InvariantCulture), now, ct);
        await UpsertSystemSettingAsync(ContractNumberStrategySettingKey, allocationStrategy, now, ct);
        await UpsertSystemSettingAsync(ContractNumberTemplateSettingKey, template, now, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(await GetContractNumberingSettingsAsync(ct));
    }

    [HttpGet("rules-settings")]
    public async Task<ActionResult<ContractRulesSettingsDto>> GetRulesSettings(CancellationToken ct)
        => Ok(await GetContractRulesSettingsAsync(ct));

    [HttpPut("rules-settings")]
    public async Task<ActionResult<ContractRulesSettingsDto>> UpsertRulesSettings([FromBody] ContractRulesSettingsRequest request, CancellationToken ct)
    {
        if (!await IsDatabaseAdminAsync(ct)) return Forbid();
        if (!request.AutoGenerateWhenEmpty && !request.AllowManualNumberEdit) return BadRequest("Нужно оставить хотя бы один способ заполнения номера договора: автогенерацию или ручной ввод.");
        var now = DateTime.UtcNow;
        await UpsertSystemSettingAsync(ContractAutoGenerateNumberSettingKey, request.AutoGenerateWhenEmpty.ToString().ToLowerInvariant(), now, ct);
        await UpsertSystemSettingAsync(ContractAllowManualNumberEditSettingKey, request.AllowManualNumberEdit.ToString().ToLowerInvariant(), now, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(await GetContractRulesSettingsAsync(ct));
    }

    [HttpGet("next-number")]
    public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct)
    {
        if (date == default) return ValidationError("date", "Дата договора обязательна.");
        var numbering = await GetContractNumberingSettingsAsync(ct);
        var rules = await GetContractRulesSettingsAsync(ct);
        var number = await GenerateNextContractNumberAsync(ContractDomainService.NormalizeLocalDate(date), excludeId, ct, numbering);
        return Ok(new NextContractNumberDto
        {
            Number = number,
            StartNumber = numbering.StartNumber,
            AllocationStrategy = numbering.AllocationStrategy,
            Template = numbering.Template,
            AutoGenerateWhenEmpty = rules.AutoGenerateWhenEmpty,
            AllowManualNumberEdit = rules.AllowManualNumberEdit,
        });
    }
    [HttpPost]
    public async Task<ActionResult<ContractDto>> Create([FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var missingCreate = await _formRequirements.GetMissingFieldsAsync("contracts", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var normalized = await ValidateAndNormalizePayload(request, null, ct);
        if (normalized.Error is not null) return normalized.Error;
        var status = NormalizeContractStatusOrNull(request.Status) ?? ContractStatus.Draft;
        if (!string.IsNullOrWhiteSpace(request.Status) && !ContractStatus.IsValid(request.Status))
        {
            return ValidationError(nameof(request.Status), "Unknown contract status.");
        }

        var contract = new Contract
        {
            Number = normalized.Number!,
            Title = Normalize(request.Title),
            Description = Normalize(request.Description),
            BillingMode = normalized.BillingMode!,
            Status = status,
            IsOpenEnded = normalized.IsOpenEnded,
            Amount = normalized.Amount,
            Date = ContractDomainService.NormalizeLocalDate(request.Date),
            StartDate = ContractDomainService.NormalizeLocalDate(request.StartDate),
            ValidUntil = normalized.ValidUntil,
            TerminationDate = ContractDomainService.NormalizeLocalDate(request.TerminationDate),
            TerminationReason = Normalize(request.TerminationReason),
            TerminationTerms = Normalize(request.TerminationTerms),
            TerminationFinancialTerms = Normalize(request.TerminationFinancialTerms),
            CancelledAt = ContractDomainService.NormalizeLocalDate(request.CancelledAt),
            CancelledReason = Normalize(request.CancelledReason),
            CreatedAt = DateTime.UtcNow,
        };

        ApplyLinks(contract, normalized.Clients, normalized.LegalEntities, normalized.ActIds, normalized.InvoiceIds, normalized.PaymentIds);
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);
        await AssignCanonicalContractLinks(contract, normalized.InvoiceIds, normalized.PaymentIds, ct);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        var createdContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractCreatedAsync(createdContract, ct);
        var dto = MapToDto(createdContract);
        return Created($"/api/contracts/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ContractDto>> Update(int id, [FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("contracts", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var visibility = await ResolveContractsVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingContractsVisibilityResult();
        }
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var contract = await _db.Contracts
            .ApplyContractVisibility(visibility)
            .Include(c => c.ClientContracts).Include(c => c.LegalEntityContracts)
            .Include(c => c.ContractActs).Include(c => c.ContractInvoices).Include(c => c.ContractPayments)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var originalStatus = contract.Status;
        var originalValidUntil = contract.ValidUntil;
        var originalAmount = contract.Amount;
        var normalized = await ValidateAndNormalizePayload(request, id, ct);
        if (normalized.Error is not null) return normalized.Error;
        var status = NormalizeContractStatusOrNull(request.Status) ?? contract.Status;
        if (!string.IsNullOrWhiteSpace(request.Status) && !ContractStatus.IsValid(request.Status))
        {
            return ValidationError(nameof(request.Status), "Unknown contract status.");
        }

        contract.Number = normalized.Number!;
        contract.Title = Normalize(request.Title);
        contract.Description = Normalize(request.Description);
        contract.BillingMode = normalized.BillingMode!;
        contract.Status = status;
        contract.IsOpenEnded = normalized.IsOpenEnded;
        contract.Amount = normalized.Amount;
        contract.Date = ContractDomainService.NormalizeLocalDate(request.Date);
        contract.StartDate = ContractDomainService.NormalizeLocalDate(request.StartDate);
        contract.ValidUntil = normalized.ValidUntil;
        contract.TerminationDate = ContractDomainService.NormalizeLocalDate(request.TerminationDate);
        contract.TerminationReason = Normalize(request.TerminationReason);
        contract.TerminationTerms = Normalize(request.TerminationTerms);
        contract.TerminationFinancialTerms = Normalize(request.TerminationFinancialTerms);
        contract.CancelledAt = ContractDomainService.NormalizeLocalDate(request.CancelledAt);
        contract.CancelledReason = Normalize(request.CancelledReason);
        contract.UpdatedAt = DateTime.UtcNow;

        SyncClientLinks(contract, normalized.Clients);
        SyncLegalEntityLinks(contract, normalized.LegalEntities);
        SyncLinks(contract.ContractActs, normalized.ActIds, id, actId => new ContractAct { ContractId = id, ActId = actId }, (l, v) => l.ActId == v);
        SyncLinks(contract.ContractInvoices, normalized.InvoiceIds, id, invoiceId => new ContractInvoice { ContractId = id, InvoiceId = invoiceId }, (l, v) => l.InvoiceId == v);
        SyncLinks(contract.ContractPayments, normalized.PaymentIds, id, paymentId => new ContractPayment { ContractId = id, PaymentId = paymentId }, (l, v) => l.PaymentId == v);
        await AssignCanonicalContractLinks(contract, normalized.InvoiceIds, normalized.PaymentIds, ct);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        var updatedContract = await BuildContractDetailQuery().FirstAsync(c => c.Id == contract.Id, ct);
        await NotifyContractChangedAsync(
            updatedContract,
            BuildContractChangeLabels(originalStatus, updatedContract.Status, originalValidUntil, updatedContract.ValidUntil, originalAmount, updatedContract.Amount),
            ct);
        var dto = MapToDto(updatedContract);
        return Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var contract = await LoadEditableContractAsync(id, ct);
        if (contract is null) return NotFound();
        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Contracts, permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }
        _db.Contracts.Remove(contract);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<Contract> BuildContractsQuery() => _db.Contracts.AsNoTracking()
        .Include(c => c.ClientContracts).ThenInclude(cc => cc.Client)
        .Include(c => c.LegalEntityContracts).ThenInclude(link => link.LegalEntity)
        .Include(c => c.ContractActs).Include(c => c.ContractInvoices).Include(c => c.ContractPayments);

    private IQueryable<Contract> BuildContractsListCountQuery(DataVisibilityAccess? visibility = null)
    {
        var query = _db.Contracts.AsNoTracking();
        return visibility is null ? query : query.ApplyContractVisibility(visibility);
    }

    private static readonly Expression<Func<Contract, ContractDto>> MapToDtoExpression = contract => new ContractDto
    {
        Id = contract.Id,
        Number = contract.Number,
        Title = contract.Title,
        Description = contract.Description,
        BillingMode = contract.BillingMode,
        Status = contract.Status,
        SentForSigningAt = ContractDomainService.NormalizeUtcInstant(contract.SentForSigningAt),
        ConcludedAt = ContractDomainService.NormalizeUtcInstant(contract.ConcludedAt),
        IsOpenEnded = contract.IsOpenEnded,
        Amount = contract.Amount,
        Date = ContractDomainService.NormalizeLocalDate(contract.Date),
        StartDate = ContractDomainService.NormalizeLocalDate(contract.StartDate),
        ValidUntil = ContractDomainService.NormalizeLocalDate(contract.ValidUntil),
        TerminationDate = ContractDomainService.NormalizeLocalDate(contract.TerminationDate),
        TerminationReason = contract.TerminationReason,
        TerminationTerms = contract.TerminationTerms,
        TerminationFinancialTerms = contract.TerminationFinancialTerms,
        CancelledAt = ContractDomainService.NormalizeLocalDate(contract.CancelledAt),
        CancelledReason = contract.CancelledReason,
        CreatedAt = ContractDomainService.NormalizeUtcInstant(contract.CreatedAt),
        UpdatedAt = ContractDomainService.NormalizeUtcInstant(contract.UpdatedAt),
        AuditCreatedByUserId = EF.Property<int?>(contract, "CreatedByUserId"),
        AuditCreatedByUserName = EF.Property<string?>(contract, "CreatedByUserName"),
        AuditUpdatedAt = ContractDomainService.NormalizeUtcInstant(EF.Property<DateTime?>(contract, "UpdatedAt")),
        AuditUpdatedByUserId = EF.Property<int?>(contract, "UpdatedByUserId"),
        AuditUpdatedByUserName = EF.Property<string?>(contract, "UpdatedByUserName"),
        AuditIsDeleted = EF.Property<bool>(contract, "IsDeleted"),
        AuditDeletedAt = ContractDomainService.NormalizeUtcInstant(EF.Property<DateTime?>(contract, "DeletedAt")),
        AuditDeletedByUserId = EF.Property<int?>(contract, "DeletedByUserId"),
        AuditDeletedByUserName = EF.Property<string?>(contract, "DeletedByUserName"),
        InvoiceAmountTotal = contract.ContractInvoices.Sum(x => (decimal?)x.Invoice.Amount) ?? 0m,
        ActAmountTotal = contract.ContractActs.Sum(x => (decimal?)x.Act.Amount) ?? 0m,
        PaymentAmountTotal = contract.ContractPayments.Sum(x => (decimal?)x.Payment.Amount) ?? 0m,
        Clients = contract.ClientContracts.Where(link => link.Client != null).Select(link => new ContractClientDto
        {
            Id = link.ClientId,
            Name = link.Client!.Name,
            Company = link.Client!.Company,
            Comment = link.Comment,
        }).OrderBy(c => c.Name).ToList(),
        LegalEntities = contract.LegalEntityContracts.Where(link => link.LegalEntity != null).Select(link => new ContractLegalEntityDto
        {
            Id = link.LegalEntityId,
            ShortName = link.LegalEntity!.ShortName,
            FullName = link.LegalEntity!.FullName,
            Inn = link.LegalEntity!.Inn,
            Kpp = link.LegalEntity!.Kpp,
            Comment = link.Comment,
        }).OrderBy(entity => entity.ShortName).ToList(),
        ActIds = contract.ContractActs.Select(x => x.ActId).ToList(),
        InvoiceIds = contract.ContractInvoices.Select(x => x.InvoiceId).ToList(),
        PaymentIds = contract.ContractPayments.Select(x => x.PaymentId).ToList(),
    };

    private IQueryable<Contract> BuildContractDetailQuery(DataVisibilityAccess? visibility = null)
    {
        var query = _db.Contracts
            .AsSplitQuery()
            .Include(c => c.ClientContracts).ThenInclude(link => link.Client)
            .Include(c => c.LegalEntityContracts).ThenInclude(link => link.LegalEntity)
            .Include(c => c.ContractActs).ThenInclude(link => link.Act)
            .Include(c => c.ContractInvoices).ThenInclude(link => link.Invoice).ThenInclude(invoice => invoice!.Acts)
            .Include(c => c.ContractInvoices).ThenInclude(link => link.Invoice).ThenInclude(invoice => invoice!.InvoiceClientCases)
            .Include(c => c.ContractInvoices).ThenInclude(link => link.Invoice).ThenInclude(payment => payment.Invoice).ThenInclude(invoice => invoice!.Payments)
            .Include(c => c.ContractPayments).ThenInclude(link => link.Payment)
            .Include(c => c.Payments).ThenInclude(payment => payment.InvoiceClientCases)
            .Include(c => c.Payments).ThenInclude(payment => payment.Acts)
            .Include(c => c.Payments).ThenInclude(payment => payment.Invoice).ThenInclude(invoice => invoice!.Payments);

        return visibility is null ? query : query.ApplyContractVisibility(visibility);
    }

    private async Task<Contract?> LoadContractForDocumentAsync(int id, CancellationToken ct)
    {
        var visibility = await ResolveContractsVisibilityAsync(ct);
        if (visibility is null)
        {
            return null;
        }

        return await BuildContractDetailQuery(visibility)
            .FirstOrDefaultAsync(contract => contract.Id == id, ct);
    }

    private Task<byte[]> RenderContractPdfAsync(string documentHtml, CancellationToken ct)
    {
        if (_invoiceDocumentPdfService is null)
        {
            throw new InvalidOperationException(
                "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения.");
        }

        return _invoiceDocumentPdfService.RenderAsync(documentHtml, ct);
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
                companyId = parsedIds?.FirstOrDefault(value => value > 0);
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

    private async Task<InvoiceDocumentTemplateSettingsDto> GetContractDocumentTemplateSettingsAsync(CancellationToken ct)
    {
        var rawJson = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == ContractDocumentTemplatesSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        var storage = ContractDocumentTemplateEngine.Deserialize(rawJson);
        return storage is null
            ? ContractDocumentTemplateEngine.CreateDefaultSettings()
            : ContractDocumentTemplateEngine.CreateResponse(storage);
    }

    private async Task<InvoiceDocumentTemplateDto> GetContractDocumentTemplateAsync(string? preferredTemplateKey, CancellationToken ct)
    {
        var settings = await GetContractDocumentTemplateSettingsAsync(ct);
        return SelectContractDocumentTemplate(settings, preferredTemplateKey);
    }

    private static InvoiceDocumentTemplateDto SelectContractDocumentTemplate(
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

    private static string? ValidateContractDocumentTemplateSettings(ContractDocumentTemplateEngine.ContractDocumentTemplateSettingsStorage storage)
    {
        if (storage.Templates.Count == 0)
        {
            return "Добавьте хотя бы один шаблон договора.";
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
            return "Выберите активный шаблон договора.";
        }

        if (!storage.Templates.Any(template => string.Equals(template.Key, storage.ActiveTemplateKey, StringComparison.OrdinalIgnoreCase)))
        {
            return "Активный шаблон должен присутствовать в списке шаблонов.";
        }

        return null;
    }

    private static string? ResolveContractRecipientEmail(Contract contract)
        => contract.ClientContracts
               .Select(link => link.Client?.Email)
               .FirstOrDefault(email => !string.IsNullOrWhiteSpace(email))?.Trim()
           ?? contract.LegalEntityContracts
               .Select(link => link.LegalEntity?.Email)
               .FirstOrDefault(email => !string.IsNullOrWhiteSpace(email))?.Trim();

    private static ContractTerminationCheckDto BuildTerminationCheck(Contract contract)
    {
        var unpaidInvoices = GetContractInvoicePayments(contract)
            .Where(payment =>
                payment.Status != PaymentStatus.Cancelled
                && (payment.Status != PaymentStatus.Completed || payment.Amount > payment.PaidAmount))
            .ToList();

        return new ContractTerminationCheckDto
        {
            HasUnpaidInvoices = unpaidInvoices.Count > 0,
            UnpaidInvoiceCount = unpaidInvoices.Count,
            UnpaidInvoiceAmount = unpaidInvoices.Sum(payment => Math.Max(payment.Amount - payment.PaidAmount, 0m)),
        };
    }

    private static List<Payment> GetContractInvoicePayments(Contract contract)
    {
        var legacyInvoices = contract.ContractInvoices
            .Where(link => link.Invoice != null)
            .Select(link => link.Invoice!);
        var canonicalInvoices = contract.Payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice && payment.InvoiceId.HasValue);

        return legacyInvoices
            .Concat(canonicalInvoices)
            .GroupBy(payment => payment.Id)
            .Select(group => group.First())
            .ToList();
    }

    private async Task SendContractTerminationNotificationAsync(Contract contract, CancellationToken ct)
    {
        if (_emailService is null)
        {
            return;
        }

        if (!await IsContractTerminationClientEmailEnabledAsync(ct))
        {
            return;
        }

        var email = ResolveContractRecipientEmail(contract);
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var terminationDate = contract.TerminationDate?.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")) ?? "не указана";
        var lines = new List<string>
        {
            $"Договор № {contract.Number} расторгнут.",
            $"Дата расторжения: {terminationDate}.",
        };

        if (!string.IsNullOrWhiteSpace(contract.TerminationReason))
        {
            lines.Add($"Причина: {contract.TerminationReason.Trim()}.");
        }

        if (!string.IsNullOrWhiteSpace(contract.TerminationTerms))
        {
            lines.Add($"Условия расторжения: {contract.TerminationTerms.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(contract.TerminationFinancialTerms))
        {
            lines.Add($"Финансовые условия: {contract.TerminationFinancialTerms.Trim()}");
        }

        var fallbackSubject = $"Договор № {contract.Number} расторгнут";
        var fallbackBody = string.Join(Environment.NewLine + Environment.NewLine, lines);
        var message = _notificationService is null
            ? new NotificationMessage(fallbackSubject, fallbackBody, Purpose: NotificationPurposes.ContractClient)
            : await _notificationService.RenderTemplateAsync(
                NotificationTemplateKeys.ContractTerminationClient,
                fallbackSubject,
                fallbackBody,
                BuildContractTerminationTemplateValues(contract, terminationDate),
                purpose: NotificationPurposes.ContractClient,
                cancellationToken: ct);

        await _emailService.SendEmailAsync(
            email,
            message.Subject,
            message.Body,
            cancellationToken: ct);
    }

    private static Dictionary<string, string?> BuildContractTerminationTemplateValues(Contract contract, string terminationDate)
    {
        var reason = contract.TerminationReason?.Trim() ?? string.Empty;
        var terms = contract.TerminationTerms?.Trim() ?? string.Empty;
        var financialTerms = contract.TerminationFinancialTerms?.Trim() ?? string.Empty;
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["contract.number"] = contract.Number,
            ["contract.terminationDate"] = terminationDate,
            ["contract.reason"] = reason,
            ["contract.terminationTerms"] = terms,
            ["contract.financialTerms"] = financialTerms,
            ["contract.reasonBlock"] = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Причина: {reason}.{Environment.NewLine}",
            ["contract.termsBlock"] = string.IsNullOrWhiteSpace(terms) ? string.Empty : $"Условия расторжения: {terms}{Environment.NewLine}",
            ["contract.financialTermsBlock"] = string.IsNullOrWhiteSpace(financialTerms) ? string.Empty : $"Финансовые условия: {financialTerms}",
        };
    }

    private async Task<bool> IsContractTerminationClientEmailEnabledAsync(CancellationToken ct)
    {
        var value = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == ContractTerminationClientEmailsEnabledKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        return bool.TryParse(value, out var enabled) && enabled;
    }

    private void ApplyContractCancellationSideEffects(Contract contract, string reason, DateTime now, TimeZoneInfo contractTimeZone)
    {
        var actorId = User?.GetUserId();
        var actorName = User?.Identity?.Name;

        foreach (var invoice in GetContractInvoicePayments(contract)
                     .Where(payment => payment.Status is not PaymentStatus.Completed and not PaymentStatus.Cancelled
                                       && payment.PaidAmount <= 0m))
        {
            invoice.Status = PaymentStatus.Cancelled;
            invoice.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled;
            invoice.IsPaid = false;
            invoice.PaidAmount = 0m;
            invoice.LastPaymentDate = null;
            invoice.InvoiceCancelledAt ??= now;
            invoice.InvoiceCancelledByUserId ??= actorId;
            invoice.InvoiceCancelledReason = reason;
            ClearInvoiceCaseLinks(invoice);
            PaymentDomainService.AppendSystemNote(
                invoice,
                $"Счёт аннулирован вместе с договором № {contract.Number}. Причина: {reason}",
                now,
                actorName,
                contractTimeZone);
        }

        var draftActs = GetContractDraftActs(contract);
        if (draftActs.Count > 0)
        {
            _db.Acts.RemoveRange(draftActs);
        }
    }

    private static void ClearInvoiceCaseLinks(Payment invoice)
    {
        invoice.ClientCaseId = null;
        invoice.ClientCase = null;
        invoice.ClientCaseReference = null;
        invoice.InvoiceClientCases.Clear();
    }

    private static List<Act> GetContractDraftActs(Contract contract)
    {
        var directActs = contract.ContractActs
            .Where(link => link.Act != null)
            .Select(link => link.Act!);
        var invoiceActs = GetContractInvoicePayments(contract)
            .SelectMany(invoice => invoice.Acts);
        var canonicalPaymentActs = contract.Payments
            .SelectMany(payment => payment.Acts);

        return directActs
            .Concat(invoiceActs)
            .Concat(canonicalPaymentActs)
            .Where(act => string.Equals(act.WorkflowStatus, ActWorkflowStatus.Draft, StringComparison.OrdinalIgnoreCase))
            .GroupBy(act => act.Id)
            .Select(group => group.First())
            .ToList();
    }

    private ContractDto MapToDto(Contract contract)
    {
        var entry = _db.Entry(contract);
        var legacyInvoiceIds = contract.ContractInvoices.Select(link => link.InvoiceId).ToHashSet();
        var canonicalInvoicePayments = contract.Payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice && payment.InvoiceId.HasValue)
            .ToList();
        var invoiceIds = legacyInvoiceIds
            .Concat(canonicalInvoicePayments.Select(payment => payment.Id))
            .Distinct()
            .ToList();

        var legacyInvoiceAmount = contract.ContractInvoices.Sum(link => link.Invoice?.Amount ?? 0m);
        var canonicalInvoiceAmount = canonicalInvoicePayments
            .Where(payment => !legacyInvoiceIds.Contains(payment.Id))
            .Sum(payment => payment.Amount);
        var legacyActIds = contract.ContractActs.Select(link => link.ActId).ToHashSet();
        var legacyActAmount = contract.ContractActs.Sum(link => link.Act?.Amount ?? 0m);
        var invoiceLinkedActs = contract.ContractInvoices
            .Where(link => link.Invoice != null)
            .SelectMany(link => link.Invoice!.Acts)
            .Concat(
                contract.Payments
                    .Where(payment => payment.DocumentKind == DocumentKind.Invoice && payment.InvoiceId.HasValue)
                    .SelectMany(payment => payment.Acts))
            .Where(act => !legacyActIds.Contains(act.Id))
            .GroupBy(act => act.Id)
            .Select(group => group.First())
            .ToList();
        var contractInvoicePayments = contract.ContractInvoices
            .Where(link => link.Invoice != null)
            .Select(link => link.Invoice)
            .Concat(canonicalInvoicePayments)
            .GroupBy(payment => payment.Id)
            .Select(group => group.First())
            .ToList();
        var contractInvoiceEntityIds = contractInvoicePayments
            .Select(payment => payment.InvoiceId ?? payment.Invoice?.Id)
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .ToHashSet();
        var invoiceLinkedPayments = contractInvoicePayments
            .Where(payment => payment.Invoice != null)
            .SelectMany(payment => payment.Invoice!.Payments)
            .Where(payment =>
                payment.DocumentKind != DocumentKind.Invoice
                && payment.InvoiceId.HasValue
                && contractInvoiceEntityIds.Contains(payment.InvoiceId.Value))
            .GroupBy(payment => payment.Id)
            .Select(group => group.First())
            .ToList();
        var legacyPaymentLinks = contract.ContractPayments
            .Where(link => link.Payment != null && link.Payment.DocumentKind != DocumentKind.Invoice)
            .ToList();
        var legacyPaymentIds = legacyPaymentLinks.Select(link => link.PaymentId).ToHashSet();
        var canonicalPayments = contract.Payments
            .Where(payment => payment.DocumentKind != DocumentKind.Invoice)
            .ToList();
        var directPaymentIds = legacyPaymentIds
            .Concat(canonicalPayments.Select(payment => payment.Id))
            .ToHashSet();
        var invoiceLinkedPaymentIds = invoiceLinkedPayments
            .Where(payment => !directPaymentIds.Contains(payment.Id))
            .Select(payment => payment.Id)
            .ToList();
        var paymentIds = legacyPaymentIds
            .Concat(canonicalPayments.Select(payment => payment.Id))
            .Concat(invoiceLinkedPaymentIds)
            .Distinct()
            .ToList();
        var legacyPaymentAmount = legacyPaymentLinks.Sum(link => link.Payment?.Amount ?? 0m);
        var canonicalPaymentAmount = canonicalPayments
            .Where(payment => !legacyPaymentIds.Contains(payment.Id))
            .Sum(payment => payment.Amount);
        var invoiceLinkedPaymentAmount = invoiceLinkedPayments
            .Where(payment => !directPaymentIds.Contains(payment.Id))
            .Sum(payment => payment.Amount);

        return new ContractDto
        {
            Id = contract.Id,
            Number = contract.Number,
            Title = contract.Title,
            Description = contract.Description,
            BillingMode = contract.BillingMode,
            Status = contract.Status,
            SentForSigningAt = ContractDomainService.NormalizeUtcInstant(contract.SentForSigningAt),
            ConcludedAt = ContractDomainService.NormalizeUtcInstant(contract.ConcludedAt),
            IsOpenEnded = contract.IsOpenEnded,
            Amount = contract.Amount,
            Date = ContractDomainService.NormalizeLocalDate(contract.Date),
            StartDate = ContractDomainService.NormalizeLocalDate(contract.StartDate),
            ValidUntil = ContractDomainService.NormalizeLocalDate(contract.ValidUntil),
            TerminationDate = ContractDomainService.NormalizeLocalDate(contract.TerminationDate),
            TerminationReason = contract.TerminationReason,
            TerminationTerms = contract.TerminationTerms,
            TerminationFinancialTerms = contract.TerminationFinancialTerms,
            CancelledAt = ContractDomainService.NormalizeLocalDate(contract.CancelledAt),
            CancelledReason = contract.CancelledReason,
            CreatedAt = ContractDomainService.NormalizeUtcInstant(contract.CreatedAt),
            UpdatedAt = ContractDomainService.NormalizeUtcInstant(contract.UpdatedAt),
            AuditCreatedByUserId = entry.Property<int?>("CreatedByUserId").CurrentValue,
            AuditCreatedByUserName = entry.Property<string?>("CreatedByUserName").CurrentValue,
            AuditUpdatedAt = ContractDomainService.NormalizeUtcInstant(entry.Property<DateTime?>("UpdatedAt").CurrentValue),
            AuditUpdatedByUserId = entry.Property<int?>("UpdatedByUserId").CurrentValue,
            AuditUpdatedByUserName = entry.Property<string?>("UpdatedByUserName").CurrentValue,
            AuditIsDeleted = entry.Property<bool>("IsDeleted").CurrentValue,
            AuditDeletedAt = ContractDomainService.NormalizeUtcInstant(entry.Property<DateTime?>("DeletedAt").CurrentValue),
            AuditDeletedByUserId = entry.Property<int?>("DeletedByUserId").CurrentValue,
            AuditDeletedByUserName = entry.Property<string?>("DeletedByUserName").CurrentValue,
            InvoiceAmountTotal = legacyInvoiceAmount + canonicalInvoiceAmount,
            ActAmountTotal = legacyActAmount + invoiceLinkedActs.Sum(act => act.Amount),
            PaymentAmountTotal = legacyPaymentAmount + canonicalPaymentAmount + invoiceLinkedPaymentAmount,
            Clients = contract.ClientContracts
                .Where(link => link.Client != null)
                .Select(link => new ContractClientDto
                {
                    Id = link.ClientId,
                    Name = link.Client!.Name,
                    Company = link.Client!.Company,
                    Comment = link.Comment,
                })
                .OrderBy(item => item.Name)
                .ToList(),
            LegalEntities = contract.LegalEntityContracts
                .Where(link => link.LegalEntity != null)
                .Select(link => new ContractLegalEntityDto
                {
                    Id = link.LegalEntityId,
                    ShortName = link.LegalEntity!.ShortName,
                    FullName = link.LegalEntity!.FullName,
                    Inn = link.LegalEntity!.Inn,
                    Kpp = link.LegalEntity!.Kpp,
                    Comment = link.Comment,
                })
                .OrderBy(item => item.ShortName)
                .ToList(),
            ActIds = contract.ContractActs.Select(link => link.ActId).ToList(),
            InvoiceIds = invoiceIds,
            PaymentIds = paymentIds,
        };
    }

    private async Task<(ActionResult? Error, string? Number, string? BillingMode, bool IsOpenEnded, decimal? Amount, DateTime? ValidUntil, List<(int ClientId, string? Comment)> Clients, List<(int LegalEntityId, string? Comment)> LegalEntities, List<int> ActIds, List<int> InvoiceIds, List<int> PaymentIds)>
        ValidateAndNormalizePayload(UpsertContractRequest request, int? contractId, CancellationToken ct)
    {
        if (request.Date == default)
        {
            return (ValidationError(nameof(request.Date), "Дата договора обязательна."), null, null, false, null, null, [], [], [], [], []);
        }
        if (!ContractBillingMode.IsValid(request.BillingMode))
        {
            return (ValidationError(nameof(request.BillingMode), "Некорректный режим договора."), null, null, false, null, null, [], [], [], [], []);
        }
        var billingMode = ContractBillingMode.Normalize(request.BillingMode);
        var isOpenEnded = request.IsOpenEnded;
        var normalizedAmount = billingMode == ContractBillingMode.Fixed ? request.Amount : null;
        var normalizedValidUntil = isOpenEnded ? null : ContractDomainService.NormalizeLocalDate(request.ValidUntil);
        if (billingMode == ContractBillingMode.Fixed && normalizedAmount is null)
        {
            return (ValidationError(nameof(request.Amount), "Для договора с фиксированной суммой нужно указать сумму."), null, null, false, null, null, [], [], [], [], []);
        }
        var rules = await GetContractRulesSettingsAsync(ct);
        var number = Normalize(request.Number);
        if (!rules.AllowManualNumberEdit)
        {
            if (contractId.HasValue && contractId.Value > 0)
            {
                number = await _db.Contracts.AsNoTracking()
                    .Where(x => x.Id == contractId.Value)
                    .Select(x => x.Number)
                    .FirstOrDefaultAsync(ct);
            }
            if (string.IsNullOrEmpty(number) && rules.AutoGenerateWhenEmpty)
            {
                number = await GenerateNextContractNumberAsync(ContractDomainService.NormalizeLocalDate(request.Date), contractId, ct);
            }
        }
        if (string.IsNullOrEmpty(number) && rules.AutoGenerateWhenEmpty)
        {
            number = await GenerateNextContractNumberAsync(ContractDomainService.NormalizeLocalDate(request.Date), contractId, ct);
        }
        if (string.IsNullOrEmpty(number))
        {
            return (ValidationError(nameof(request.Number), "Номер договора обязателен."), null, null, false, null, null, [], [], [], [], []);
        }
        var duplicateNumberError = await EnsureContractNumberIsUniqueAsync(number, contractId, ct);
        if (duplicateNumberError is not null)
        {
            return (duplicateNumberError, null, null, false, null, null, [], [], [], [], []);
        }
        var clients = request.Clients?.Where(x => x.ClientId > 0).GroupBy(x => x.ClientId)
            .Select(g => (ClientId: g.Key, Comment: Normalize(g.Last().Comment))).ToList() ?? [];
        var legalEntities = request.LegalEntities?.Where(x => x.LegalEntityId > 0).GroupBy(x => x.LegalEntityId)
            .Select(g => (LegalEntityId: g.Key, Comment: Normalize(g.Last().Comment))).ToList() ?? [];
        if (clients.Count == 0 && legalEntities.Count == 0)
        {
            return (BadRequest("Необходимо выбрать хотя бы одного контрагента."), null, null, false, null, null, [], [], [], [], []);
        }
        var clientIds = clients.Select(x => x.ClientId).ToList();
        var existingClients = await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        if (existingClients.Count != clientIds.Count)
        {
            return (BadRequest($"Не найдены клиенты: {string.Join(", ", clientIds.Except(existingClients))}"), null, null, false, null, null, [], [], [], [], []);
        }
        var legalEntityIds = legalEntities.Select(x => x.LegalEntityId).ToList();
        var existingLegalEntities = await _db.LegalEntities.AsNoTracking().Where(entity => legalEntityIds.Contains(entity.Id)).Select(entity => entity.Id).ToListAsync(ct);
        if (existingLegalEntities.Count != legalEntityIds.Count)
        {
            return (BadRequest($"Не найдены юридические лица: {string.Join(", ", legalEntityIds.Except(existingLegalEntities))}"), null, null, false, null, null, [], [], [], [], []);
        }
        var actIds = NormalizeIds(request.ActIds);
        if (actIds.Count > 0)
        {
            var existing = await _db.Acts.AsNoTracking().Where(x => actIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != actIds.Count)
            {
                return (BadRequest($"Не найдены акты: {string.Join(", ", actIds.Except(existing))}"), null, null, false, null, null, [], [], [], [], []);
            }
        }
        var invoiceIds = NormalizeIds(request.InvoiceIds);
        if (invoiceIds.Count > 0)
        {
            var existing = await _db.Payments.AsNoTracking()
                .Where(x => invoiceIds.Contains(x.Id) && x.Account != null && x.Account != string.Empty && x.DocumentKind == DocumentKind.Invoice)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (existing.Count != invoiceIds.Count)
            {
                return (BadRequest($"Не найдены счета: {string.Join(", ", invoiceIds.Except(existing))}"), null, null, false, null, null, [], [], [], [], []);
            }
        }
        var paymentIds = NormalizeIds(request.PaymentIds);
        if (paymentIds.Count > 0)
        {
            var existing = await _db.Payments.AsNoTracking().Where(x => paymentIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != paymentIds.Count)
            {
                return (BadRequest($"Не найдены платежи: {string.Join(", ", paymentIds.Except(existing))}"), null, null, false, null, null, [], [], [], [], []);
            }
        }
        if (billingMode == ContractBillingMode.Fixed && normalizedAmount.HasValue)
        {
            var invoiceAmountTotal = invoiceIds.Count == 0
                ? 0m
                : await _db.Payments.AsNoTracking()
                    .Where(x => invoiceIds.Contains(x.Id))
                    .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
            if (invoiceAmountTotal > normalizedAmount.Value)
            {
                return (ValidationError(nameof(request.Amount), "Сумма привязанных счетов превышает сумму договора."), null, null, false, null, null, [], [], [], [], []);
            }
        }
        return (null, number, billingMode, isOpenEnded, normalizedAmount, normalizedValidUntil, clients, legalEntities, actIds, invoiceIds, paymentIds);
    }
    private async Task<ActionResult?> EnsureContractNumberIsUniqueAsync(string number, int? excludeId, CancellationToken ct)
    {
        var exists = await _db.Contracts.AsNoTracking()
            .AnyAsync(c => c.Number == number && (!excludeId.HasValue || c.Id != excludeId.Value), ct);

        return exists
            ? ValidationError(nameof(UpsertContractRequest.Number), "Договор с таким номером уже существует.")
            : null;
    }

    private async Task<string> GenerateNextContractNumberAsync(DateTime date, int? excludeId, CancellationToken ct, ContractNumberingSettingsDto? settings = null)
    {
        settings ??= await GetContractNumberingSettingsAsync(ct);
        var existingNumbers = await _db.Contracts.AsNoTracking()
            .Where(c => !excludeId.HasValue || c.Id != excludeId.Value)
            .Select(c => c.Number)
            .ToListAsync(ct);

        var existingSequences = existingNumbers
            .Select(number => TryParseGeneratedContractNumber(number, date, settings.Template, out var sequence) ? sequence : 0)
            .Where(sequence => sequence > 0)
            .Distinct()
            .OrderBy(sequence => sequence)
            .ToList();

        var nextSequence = ResolveNextSequence(existingSequences, settings.StartNumber, settings.AllocationStrategy);
        return FormatGeneratedContractNumber(nextSequence, date, settings.Template);
    }

    private static int ResolveNextSequence(IEnumerable<int> existingSequences, int startNumber, string allocationStrategy)
    {
        var normalizedStart = Math.Max(1, startNumber);
        var sorted = existingSequences.Where(sequence => sequence >= normalizedStart).Distinct().OrderBy(sequence => sequence).ToList();
        if (ParseContractAllocationStrategy(allocationStrategy) == "first-free")
        {
            var candidate = normalizedStart;
            foreach (var sequence in sorted)
            {
                if (sequence < candidate) continue;
                if (sequence == candidate)
                {
                    candidate++;
                    continue;
                }

                break;
            }

            return candidate;
        }

        return sorted.Count == 0 ? normalizedStart : sorted[^1] + 1;
    }

    private static string FormatGeneratedContractNumber(int sequence, DateTime date, string template)
    {
        var normalizedTemplate = NormalizeContractTemplate(template);
        if (!TryExtractSequenceToken(normalizedTemplate, out var sequenceToken))
        {
            normalizedTemplate = DefaultContractNumberTemplate;
            TryExtractSequenceToken(normalizedTemplate, out sequenceToken);
        }

        var sequenceWidth = Math.Max(1, sequenceToken.Length - 2);
        var sequenceText = sequenceWidth == 1
            ? sequence.ToString(CultureInfo.InvariantCulture)
            : sequence.ToString(CultureInfo.InvariantCulture).PadLeft(sequenceWidth, '0');

        return RenderContractTemplate(normalizedTemplate, date, sequenceText);
    }

    private static bool TryParseGeneratedContractNumber(string? number, DateTime date, string template, out int sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(number)) return false;

        var normalizedTemplate = NormalizeContractTemplate(template);
        if (!TryExtractSequenceToken(normalizedTemplate, out var sequenceToken)) return false;

        var tokenIndex = normalizedTemplate.IndexOf(sequenceToken, StringComparison.Ordinal);
        var prefix = RenderContractTemplate(normalizedTemplate[..tokenIndex], date, string.Empty);
        var suffix = RenderContractTemplate(normalizedTemplate[(tokenIndex + sequenceToken.Length)..], date, string.Empty);
        if (!number.StartsWith(prefix, StringComparison.Ordinal) || !number.EndsWith(suffix, StringComparison.Ordinal)) return false;

        var middleLength = number.Length - prefix.Length - suffix.Length;
        if (middleLength <= 0) return false;

        var middle = number.Substring(prefix.Length, middleLength);
        return int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence > 0;
    }

    private async Task<ContractNumberingSettingsDto> GetContractNumberingSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == ContractNumberStartSettingKey || x.Key == ContractNumberStrategySettingKey || x.Key == ContractNumberTemplateSettingKey)
            .ToListAsync(ct);

        var template = NormalizeContractTemplate(settings.FirstOrDefault(x => x.Key == ContractNumberTemplateSettingKey)?.Value);
        if (!TryExtractSequenceToken(template, out _)) template = DefaultContractNumberTemplate;

        return new ContractNumberingSettingsDto
        {
            StartNumber = ParsePositiveSetting(settings.FirstOrDefault(x => x.Key == ContractNumberStartSettingKey)?.Value, 1),
            AllocationStrategy = ParseContractAllocationStrategy(settings.FirstOrDefault(x => x.Key == ContractNumberStrategySettingKey)?.Value),
            Template = template,
        };
    }

    private async Task<ContractRulesSettingsDto> GetContractRulesSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == ContractAutoGenerateNumberSettingKey || x.Key == ContractAllowManualNumberEditSettingKey)
            .ToListAsync(ct);

        return new ContractRulesSettingsDto
        {
            AutoGenerateWhenEmpty = ParseBoolSetting(settings.FirstOrDefault(x => x.Key == ContractAutoGenerateNumberSettingKey)?.Value, true),
            AllowManualNumberEdit = ParseBoolSetting(settings.FirstOrDefault(x => x.Key == ContractAllowManualNumberEditSettingKey)?.Value, true),
        };
    }

    private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, CreatedAt = nowUtc, UpdatedAt = nowUtc });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private static string NormalizeContractTemplate(string? value)
    {
        var template = string.IsNullOrWhiteSpace(value) ? DefaultContractNumberTemplate : value.Trim();
        return template
            .Replace("{yyyy}", "{YYYY}", StringComparison.OrdinalIgnoreCase)
            .Replace("{yy}", "{YY}", StringComparison.OrdinalIgnoreCase)
            .Replace("{dd}", "{DD}", StringComparison.OrdinalIgnoreCase)
            .Replace("{mm}", "{MM}", StringComparison.OrdinalIgnoreCase)
            .Replace("{nnnn}", "{NNNN}", StringComparison.OrdinalIgnoreCase)
            .Replace("{nnn}", "{NNN}", StringComparison.OrdinalIgnoreCase)
            .Replace("{nn}", "{NN}", StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", "{N}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractSequenceToken(string template, out string sequenceToken)
    {
        sequenceToken = string.Empty;
        foreach (var candidate in new[] { "{NNNN}", "{NNN}", "{NN}", "{N}" })
        {
            var firstIndex = template.IndexOf(candidate, StringComparison.Ordinal);
            if (firstIndex < 0) continue;
            if (template.IndexOf(candidate, firstIndex + candidate.Length, StringComparison.Ordinal) >= 0) return false;
            var withoutCurrent = template[..firstIndex] + "{#}" + template[(firstIndex + candidate.Length)..];
            if (withoutCurrent.Contains("{NNNN}", StringComparison.Ordinal) || withoutCurrent.Contains("{NNN}", StringComparison.Ordinal) || withoutCurrent.Contains("{NN}", StringComparison.Ordinal) || withoutCurrent.Contains("{N}", StringComparison.Ordinal)) return false;
            sequenceToken = candidate;
            return true;
        }

        return false;
    }

    private static string RenderContractTemplate(string template, DateTime date, string sequenceText)
    {
        var normalizedTemplate = NormalizeContractTemplate(template)
            .Replace("{YYYY}", date.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{YY}", date.ToString("yy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{DD}", date.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", date.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal);

        if (TryExtractSequenceToken(normalizedTemplate, out var sequenceToken))
        {
            normalizedTemplate = normalizedTemplate.Replace(sequenceToken, sequenceText, StringComparison.Ordinal);
        }

        return normalizedTemplate;
    }

    private static string ParseContractAllocationStrategy(string? value)
        => string.Equals(value, "first-free", StringComparison.OrdinalIgnoreCase) ? "first-free" : "max-plus-one";

    private static int ParsePositiveSetting(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;

    private static bool ParseBoolSetting(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static void ApplyLinks(
        Contract contract,
        List<(int ClientId, string? Comment)> clients,
        List<(int LegalEntityId, string? Comment)> legalEntities,
        List<int> actIds,
        List<int> invoiceIds,
        List<int> paymentIds)
    {
        foreach (var client in clients) contract.ClientContracts.Add(new ClientContract { ClientId = client.ClientId, Comment = client.Comment });
        foreach (var entity in legalEntities) contract.LegalEntityContracts.Add(new LegalEntityContract { LegalEntityId = entity.LegalEntityId, Comment = entity.Comment });
        foreach (var actId in actIds) contract.ContractActs.Add(new ContractAct { ActId = actId });
        foreach (var invoiceId in invoiceIds) contract.ContractInvoices.Add(new ContractInvoice { InvoiceId = invoiceId });
        foreach (var paymentId in paymentIds) contract.ContractPayments.Add(new ContractPayment { PaymentId = paymentId });
    }

    private static void SyncClientLinks(Contract contract, List<(int ClientId, string? Comment)> clients)
    {
        var requested = clients.ToDictionary(x => x.ClientId, x => x.Comment);
        foreach (var link in contract.ClientContracts.Where(x => !requested.ContainsKey(x.ClientId)).ToList()) contract.ClientContracts.Remove(link);
        foreach (var (clientId, comment) in requested)
        {
            var existing = contract.ClientContracts.FirstOrDefault(x => x.ClientId == clientId);
            if (existing is null) contract.ClientContracts.Add(new ClientContract { ContractId = contract.Id, ClientId = clientId, Comment = comment });
            else existing.Comment = comment;
        }
    }

    private static void SyncLegalEntityLinks(Contract contract, List<(int LegalEntityId, string? Comment)> legalEntities)
    {
        var requested = legalEntities.ToDictionary(x => x.LegalEntityId, x => x.Comment);
        foreach (var link in contract.LegalEntityContracts.Where(x => !requested.ContainsKey(x.LegalEntityId)).ToList()) contract.LegalEntityContracts.Remove(link);
        foreach (var (legalEntityId, comment) in requested)
        {
            var existing = contract.LegalEntityContracts.FirstOrDefault(x => x.LegalEntityId == legalEntityId);
            if (existing is null) contract.LegalEntityContracts.Add(new LegalEntityContract { ContractId = contract.Id, LegalEntityId = legalEntityId, Comment = comment });
            else existing.Comment = comment;
        }
    }

    private static void SyncLinks<TLink>(ICollection<TLink> existing, List<int> targetIds, int contractId, Func<int, TLink> create, Func<TLink, int, bool> match)
    {
        foreach (var link in existing.Where(link => !targetIds.Any(id => match(link, id))).ToList()) existing.Remove(link);
        foreach (var id in targetIds)
            if (!existing.Any(link => match(link, id))) existing.Add(create(id));
    }

    private async Task AssignCanonicalContractLinks(Contract contract, List<int> invoiceIds, List<int> paymentIds, CancellationToken ct)
    {
        var invoiceTargets = invoiceIds.Distinct().ToHashSet();
        var invoiceCandidates = await _db.Payments
            .Where(x => x.DocumentKind == DocumentKind.Invoice && (x.ContractId == contract.Id || invoiceTargets.Contains(x.Id)))
            .ToListAsync(ct);

        foreach (var payment in invoiceCandidates)
        {
            payment.ContractId = invoiceTargets.Contains(payment.Id) ? contract.Id : null;
        }

        var paymentTargets = paymentIds.Distinct().ToHashSet();
        var paymentCandidates = await _db.Payments
            .Where(x => x.DocumentKind != DocumentKind.Invoice && (x.ContractId == contract.Id || paymentTargets.Contains(x.Id)))
            .ToListAsync(ct);

        foreach (var payment in paymentCandidates)
        {
            payment.ContractId = paymentTargets.Contains(payment.Id) ? contract.Id : null;
        }
    }

    private static IQueryable<Contract> ApplyFilters(
        IQueryable<Contract> query,
        DateTime? from,
        DateTime? to,
        int? clientId,
        int? legalEntityId,
        int? invoiceId,
        int? actId,
        int? paymentId,
        string? status,
        string? search)
    {
        if (from.HasValue) query = query.Where(c => c.Date >= from.Value.Date);
        if (to.HasValue) query = query.Where(c => c.Date <= to.Value.Date);
        if (clientId.HasValue) query = query.Where(c => c.ClientContracts.Any(link => link.ClientId == clientId.Value));
        if (legalEntityId.HasValue) query = query.Where(c => c.LegalEntityContracts.Any(link => link.LegalEntityId == legalEntityId.Value));
        if (invoiceId.HasValue)
        {
            query = query.Where(c =>
                c.ContractInvoices.Any(link => link.InvoiceId == invoiceId.Value)
                || c.Payments.Any(payment => payment.Id == invoiceId.Value && payment.DocumentKind == DocumentKind.Invoice));
        }
        if (actId.HasValue)
        {
            query = query.Where(c =>
                c.ContractActs.Any(link => link.ActId == actId.Value)
                || c.ContractInvoices.Any(link => link.Invoice != null && link.Invoice.Acts.Any(act => act.Id == actId.Value))
                || c.Payments.Any(payment => payment.DocumentKind == DocumentKind.Invoice && payment.Acts.Any(act => act.Id == actId.Value)));
        }
        if (paymentId.HasValue)
        {
            query = query.Where(c =>
                c.ContractPayments.Any(link => link.PaymentId == paymentId.Value)
                || c.Payments.Any(payment => payment.Id == paymentId.Value && payment.DocumentKind != DocumentKind.Invoice));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var rawTerm = search.Trim();
            var term = rawTerm.ToLower(CultureInfo.InvariantCulture);
            var upperTerm = rawTerm.ToUpper(CultureInfo.InvariantCulture);
            var titleTerm = CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(term);
            query = query.Where(c =>
                c.Number.ToLower().Contains(term) ||
                c.Number.Contains(rawTerm) ||
                c.Number.Contains(upperTerm) ||
                c.Number.Contains(titleTerm) ||
                (c.Title != null &&
                    (c.Title.ToLower().Contains(term) ||
                     c.Title.Contains(rawTerm) ||
                     c.Title.Contains(upperTerm) ||
                     c.Title.Contains(titleTerm))) ||
                (c.Description != null &&
                    (c.Description.ToLower().Contains(term) ||
                     c.Description.Contains(rawTerm) ||
                     c.Description.Contains(upperTerm) ||
                     c.Description.Contains(titleTerm))) ||
                c.ClientContracts.Any(link =>
                    link.Client != null &&
                    (link.Client.Name.ToLower().Contains(term) ||
                     link.Client.Name.Contains(rawTerm) ||
                     link.Client.Name.Contains(upperTerm) ||
                     link.Client.Name.Contains(titleTerm) ||
                     (link.Client.Company != null &&
                        (link.Client.Company.ToLower().Contains(term) ||
                         link.Client.Company.Contains(rawTerm) ||
                         link.Client.Company.Contains(upperTerm) ||
                         link.Client.Company.Contains(titleTerm))))) ||
                c.LegalEntityContracts.Any(link =>
                    link.LegalEntity != null &&
                    (link.LegalEntity.ShortName.ToLower().Contains(term) ||
                     link.LegalEntity.ShortName.Contains(rawTerm) ||
                     link.LegalEntity.ShortName.Contains(upperTerm) ||
                     link.LegalEntity.ShortName.Contains(titleTerm) ||
                     (link.LegalEntity.FullName != null &&
                        (link.LegalEntity.FullName.ToLower().Contains(term) ||
                         link.LegalEntity.FullName.Contains(rawTerm) ||
                         link.LegalEntity.FullName.Contains(upperTerm) ||
                         link.LegalEntity.FullName.Contains(titleTerm))) ||
                     (link.LegalEntity.Inn != null && link.LegalEntity.Inn.Contains(rawTerm)) ||
                     (link.LegalEntity.Kpp != null && link.LegalEntity.Kpp.Contains(rawTerm)))));
        }
        return query;
    }

    private static IQueryable<Contract> ApplySort(IQueryable<Contract> query, string? sortBy, string? sortDir)
    {
        var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return sortBy?.ToLowerInvariant() switch
        {
            "number" => descending ? query.OrderByDescending(c => c.Number) : query.OrderBy(c => c.Number),
            "amount" => descending ? query.OrderByDescending(c => c.Amount) : query.OrderBy(c => c.Amount),
            "createdat" => descending ? query.OrderByDescending(c => c.CreatedAt) : query.OrderBy(c => c.CreatedAt),
            "date" => descending ? query.OrderByDescending(c => c.Date) : query.OrderBy(c => c.Date),
            _ => query.OrderByDescending(c => c.Date).ThenByDescending(c => c.Id),
        };
    }

    private ActionResult ValidationError(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private static string? NormalizeContractStatusOrNull(string? value)
        => ContractStatus.Normalize(value);

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

    private static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "contract";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return value.Trim();
    }

    private static List<int> NormalizeIds(IEnumerable<int>? ids) => ids?.Where(x => x > 0).Distinct().ToList() ?? [];
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<ActionResult?> RequireSectionPermissionAsync(
        string section,
        Func<RolePermission, bool> isAllowed,
        CancellationToken ct)
    {
        var principal = User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (await IsDatabaseAdminAsync(ct))
        {
            return null;
        }

        var userId = principal.GetUserId();
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
}
