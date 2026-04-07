using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

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
    private const string DefaultContractNumberTemplate = "{N}-{DD}/{MM}-{YY}";

    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;

    public ContractsController(PaymentContext db, IFormRequirementsService formRequirements)
    {
        _db = db;
        _formRequirements = formRequirements;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId,
        [FromQuery] string? search, [FromQuery] string? sortBy, [FromQuery] string? sortDir, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        var query = ApplySort(ApplyFilters(BuildContractsQuery(), from, to, clientId, search), sortBy, sortDir);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(MapToDtoExpression).ToListAsync(ct);
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ContractDto>> GetById(int id, CancellationToken ct)
    {
        var dto = await BuildContractsQuery().Where(c => c.Id == id).Select(MapToDtoExpression).FirstOrDefaultAsync(ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpGet("numbering-settings")]
    public async Task<ActionResult<ContractNumberingSettingsDto>> GetNumberingSettings(CancellationToken ct)
        => Ok(await GetContractNumberingSettingsAsync(ct));

    [HttpPut("numbering-settings")]
    public async Task<ActionResult<ContractNumberingSettingsDto>> UpsertNumberingSettings([FromBody] ContractNumberingSettingsRequest request, CancellationToken ct)
    {
        if (!User.IsInRole("admin")) return Forbid();
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
        if (!User.IsInRole("admin")) return Forbid();
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
        var number = await GenerateNextContractNumberAsync(date.Date, excludeId, ct, numbering);
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
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("contracts", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var normalized = await ValidateAndNormalizePayload(request, null, ct);
        if (normalized.Error is not null) return normalized.Error;

        var contract = new Contract
        {
            Number = normalized.Number!,
            Title = Normalize(request.Title),
            Description = Normalize(request.Description),
            Amount = request.Amount,
            Date = request.Date.Date,
            StartDate = request.StartDate?.Date,
            ValidUntil = request.ValidUntil?.Date,
            TerminationDate = request.TerminationDate?.Date,
            CreatedAt = DateTime.UtcNow,
        };

        ApplyLinks(contract, normalized.Clients, normalized.ActIds, normalized.InvoiceIds, normalized.PaymentIds);
        await AssignCanonicalContractLinks(contract, normalized.InvoiceIds, normalized.PaymentIds, ct);
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);
        var dto = await BuildContractsQuery().Where(c => c.Id == contract.Id).Select(MapToDtoExpression).FirstAsync(ct);
        return Created($"/api/contracts/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ContractDto>> Update(int id, [FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("contracts", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var contract = await _db.Contracts
            .Include(c => c.ClientContracts).Include(c => c.ContractActs).Include(c => c.ContractInvoices).Include(c => c.ContractPayments)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var normalized = await ValidateAndNormalizePayload(request, id, ct);
        if (normalized.Error is not null) return normalized.Error;

        contract.Number = normalized.Number!;
        contract.Title = Normalize(request.Title);
        contract.Description = Normalize(request.Description);
        contract.Amount = request.Amount;
        contract.Date = request.Date.Date;
        contract.StartDate = request.StartDate?.Date;
        contract.ValidUntil = request.ValidUntil?.Date;
        contract.TerminationDate = request.TerminationDate?.Date;
        contract.UpdatedAt = DateTime.UtcNow;

        SyncClientLinks(contract, normalized.Clients);
        SyncLinks(contract.ContractActs, normalized.ActIds, id, actId => new ContractAct { ContractId = id, ActId = actId }, (l, v) => l.ActId == v);
        SyncLinks(contract.ContractInvoices, normalized.InvoiceIds, id, invoiceId => new ContractInvoice { ContractId = id, InvoiceId = invoiceId }, (l, v) => l.InvoiceId == v);
        SyncLinks(contract.ContractPayments, normalized.PaymentIds, id, paymentId => new ContractPayment { ContractId = id, PaymentId = paymentId }, (l, v) => l.PaymentId == v);
        await AssignCanonicalContractLinks(contract, normalized.InvoiceIds, normalized.PaymentIds, ct);
        await _db.SaveChangesAsync(ct);
        var dto = await BuildContractsQuery().Where(c => c.Id == contract.Id).Select(MapToDtoExpression).FirstAsync(ct);
        return Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        _db.Contracts.Remove(contract);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<Contract> BuildContractsQuery() => _db.Contracts.AsNoTracking()
        .Include(c => c.ClientContracts).ThenInclude(cc => cc.Client)
        .Include(c => c.ContractActs).Include(c => c.ContractInvoices).Include(c => c.ContractPayments);

    private static readonly Expression<Func<Contract, ContractDto>> MapToDtoExpression = contract => new ContractDto
    {
        Id = contract.Id,
        Number = contract.Number,
        Title = contract.Title,
        Description = contract.Description,
        Amount = contract.Amount,
        Date = contract.Date,
        StartDate = contract.StartDate,
        ValidUntil = contract.ValidUntil,
        TerminationDate = contract.TerminationDate,
        CreatedAt = contract.CreatedAt,
        UpdatedAt = contract.UpdatedAt,
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
        ActIds = contract.ContractActs.Select(x => x.ActId).ToList(),
        InvoiceIds = contract.ContractInvoices.Select(x => x.InvoiceId).ToList(),
        PaymentIds = contract.ContractPayments.Select(x => x.PaymentId).ToList(),
    };

    private async Task<(ActionResult? Error, string? Number, List<(int ClientId, string? Comment)> Clients, List<int> ActIds, List<int> InvoiceIds, List<int> PaymentIds)>
        ValidateAndNormalizePayload(UpsertContractRequest request, int? contractId, CancellationToken ct)
    {
        if (request.Date == default)
        {
            return (ValidationError(nameof(request.Date), "Дата договора обязательна."), null, [], [], [], []);
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
                number = await GenerateNextContractNumberAsync(request.Date.Date, contractId, ct);
            }
        }

        if (string.IsNullOrEmpty(number) && rules.AutoGenerateWhenEmpty)
        {
            number = await GenerateNextContractNumberAsync(request.Date.Date, contractId, ct);
        }

        if (string.IsNullOrEmpty(number))
        {
            return (ValidationError(nameof(request.Number), "Номер договора обязателен."), null, [], [], [], []);
        }

        var duplicateNumberError = await EnsureContractNumberIsUniqueAsync(number, contractId, ct);
        if (duplicateNumberError is not null)
        {
            return (duplicateNumberError, null, [], [], [], []);
        }

        var clients = request.Clients?.Where(x => x.ClientId > 0).GroupBy(x => x.ClientId)
            .Select(g => (ClientId: g.Key, Comment: Normalize(g.Last().Comment))).ToList() ?? [];
        if (clients.Count == 0)
        {
            return (BadRequest("Необходимо выбрать хотя бы одного клиента."), null, [], [], [], []);
        }

        var clientIds = clients.Select(x => x.ClientId).ToList();
        var existingClients = await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        if (existingClients.Count != clientIds.Count)
        {
            return (BadRequest($"Не найдены клиенты: {string.Join(", ", clientIds.Except(existingClients))}"), null, [], [], [], []);
        }

        var actIds = NormalizeIds(request.ActIds);
        if (actIds.Count > 0)
        {
            var existing = await _db.Acts.AsNoTracking().Where(x => actIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != actIds.Count)
            {
                return (BadRequest($"Не найдены акты: {string.Join(", ", actIds.Except(existing))}"), null, [], [], [], []);
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
                return (BadRequest($"Не найдены счета: {string.Join(", ", invoiceIds.Except(existing))}"), null, [], [], [], []);
            }
        }

        var paymentIds = NormalizeIds(request.PaymentIds);
        if (paymentIds.Count > 0)
        {
            var existing = await _db.Payments.AsNoTracking().Where(x => paymentIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != paymentIds.Count)
            {
                return (BadRequest($"Не найдены начисления: {string.Join(", ", paymentIds.Except(existing))}"), null, [], [], [], []);
            }
        }

        return (null, number, clients, actIds, invoiceIds, paymentIds);
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

    private static void ApplyLinks(Contract contract, List<(int ClientId, string? Comment)> clients, List<int> actIds, List<int> invoiceIds, List<int> paymentIds)
    {
        foreach (var client in clients) contract.ClientContracts.Add(new ClientContract { ClientId = client.ClientId, Comment = client.Comment });
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

    private static void SyncLinks<TLink>(ICollection<TLink> existing, List<int> targetIds, int contractId, Func<int, TLink> create, Func<TLink, int, bool> match)
    {
        foreach (var link in existing.Where(link => !targetIds.Any(id => match(link, id))).ToList()) existing.Remove(link);
        foreach (var id in targetIds)
            if (!existing.Any(link => match(link, id))) existing.Add(create(id));
    }

    private async Task AssignCanonicalContractLinks(Contract contract, List<int> invoiceIds, List<int> paymentIds, CancellationToken ct)
    {
        var targetIds = invoiceIds.Concat(paymentIds).Distinct().ToList();
        if (targetIds.Count == 0) return;

        var payments = await _db.Payments.Where(x => targetIds.Contains(x.Id)).ToListAsync(ct);
        foreach (var payment in payments)
        {
            payment.ContractId = contract.Id;
        }
    }

    private static IQueryable<Contract> ApplyFilters(IQueryable<Contract> query, DateTime? from, DateTime? to, int? clientId, string? search)
    {
        if (from.HasValue) query = query.Where(c => c.Date >= from.Value.Date);
        if (to.HasValue) query = query.Where(c => c.Date <= to.Value.Date);
        if (clientId.HasValue) query = query.Where(c => c.ClientContracts.Any(link => link.ClientId == clientId.Value));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(c =>
                c.Number.ToLower().Contains(term) ||
                (c.Title != null && c.Title.ToLower().Contains(term)) ||
                (c.Description != null && c.Description.ToLower().Contains(term)));
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

    private static List<int> NormalizeIds(IEnumerable<int>? ids) => ids?.Where(x => x > 0).Distinct().ToList() ?? [];
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
