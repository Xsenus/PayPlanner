using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Controllers;

/// <summary>
/// Филиалы, офисы, точки и направления собственной компании. Просмотр — по доступу к профилю,
/// управление — только у действующего администратора. Филиал не имеет своего ИНН: переопределяются
/// только КПП, адрес, банковский счёт и подписант, всё остальное наследуется от компании.
/// </summary>
[ApiController]
[Route("api/legal-entities/{companyId:int}/branches")]
[Authorize]
public class CompanyBranchesController : ControllerBase
{
    private const int MaxSignerJsonLength = 8000;

    private readonly PaymentContext _db;

    public CompanyBranchesController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<CompanyBranchDto>>> List(
        int companyId,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
    {
        var denied = await CheckAccessAsync(companyId, false, ct);
        if (denied is not null) return denied;
        if (!await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Id == companyId, ct))
        {
            return NotFound();
        }

        var query = _db.CompanyBranches.AsNoTracking().Where(b => b.LegalEntityId == companyId);
        if (!includeArchived)
        {
            query = query.Where(b => b.Status != CompanyProfileStatuses.Archived);
        }

        var items = await query.OrderBy(b => b.Name).ToListAsync(ct);
        return Ok(items.Select(CompanyBranchDto.From).ToList());
    }

    [HttpPost]
    public Task<ActionResult<CompanyBranchDto>> Create(int companyId, [FromBody] CompanyBranchRequest request, CancellationToken ct)
        => MutateAsync(companyId, () => CreateCoreAsync(companyId, request, ct), ct);

    private async Task<ActionResult<CompanyBranchDto>> CreateCoreAsync(int companyId, CompanyBranchRequest request, CancellationToken ct)
    {
        var company = await LoadEditableCompanyAsync(companyId, ct);
        if (company is null)
        {
            return NotFound();
        }

        if (company.ProfileStatus == CompanyProfileStatuses.Archived)
        {
            return Conflict(new { message = "Компания в архиве: добавлять подразделения нельзя." });
        }

        var branch = new CompanyBranch { LegalEntityId = companyId, CreatedAt = DateTime.UtcNow };
        var error = await ApplyAsync(company, branch, request, ct);
        if (error is not null)
        {
            return error;
        }

        _db.CompanyBranches.Add(branch);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/legal-entities/{companyId}/branches/{branch.Id}", CompanyBranchDto.From(branch));
    }

    [HttpPut("{id:int}")]
    public Task<ActionResult<CompanyBranchDto>> Update(
        int companyId,
        int id,
        [FromBody] CompanyBranchRequest request,
        CancellationToken ct)
        => MutateAsync(companyId, () => UpdateCoreAsync(companyId, id, request, ct), ct);

    private async Task<ActionResult<CompanyBranchDto>> UpdateCoreAsync(int companyId, int id, CompanyBranchRequest request, CancellationToken ct)
    {
        var company = await LoadEditableCompanyAsync(companyId, ct);
        if (company is null) return NotFound();
        var branch = await _db.CompanyBranches
            .FirstOrDefaultAsync(b => b.Id == id && b.LegalEntityId == companyId, ct);
        if (branch is null)
        {
            return NotFound();
        }

        var error = await ApplyAsync(company, branch, request, ct);
        if (error is not null)
        {
            return error;
        }

        branch.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(CompanyBranchDto.From(branch));
    }

    /// <summary>Реквизиты, которые попадут в документ при выборе этого подразделения (с указанием, откуда взято каждое значение).</summary>
    [HttpGet("{id:int}/effective-requisites")]
    public async Task<ActionResult<CompanyEffectiveRequisitesResponse>> GetEffectiveRequisites(
        int companyId,
        int id,
        CancellationToken ct)
    {
        var denied = await CheckAccessAsync(companyId, false, ct);
        if (denied is not null) return denied;
        var branch = await _db.CompanyBranches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id && b.LegalEntityId == companyId, ct);
        if (branch is null)
        {
            return NotFound();
        }

        var company = await _db.LegalEntities
            .AsNoTracking()
            .Select(row => new LegalEntity
            {
                Id = row.Id,
                ShortName = row.ShortName,
                FullName = row.FullName,
                Inn = row.Inn,
                Kpp = row.Kpp,
                Ogrn = row.Ogrn,
                Address = row.Address,
                Addresses = row.Addresses,
                BankAccountsJson = row.BankAccountsJson,
                DocumentDirectorJson = row.DocumentDirectorJson,
                TreasuryBankName = row.TreasuryBankName,
                TreasuryBankBik = row.TreasuryBankBik,
                TreasuryAccountNumber = row.TreasuryAccountNumber,
                TreasuryCorrAccountNumber = row.TreasuryCorrAccountNumber
            })
            .FirstOrDefaultAsync(le => le.Id == companyId, ct);
        if (company is null) return NotFound();

        return Ok(new CompanyEffectiveRequisitesResponse(
            CompanyRequisitesResolver.Resolve(company, branch, DateTime.UtcNow)));
    }

    private async Task<ActionResult?> CheckAccessAsync(int companyId, bool write, CancellationToken ct)
    {
        var access = await CompanyProfileAccess.ResolveAsync(_db, User, ct);
        if (access is null || write && !access.Section.Visibility.IsAdmin) return Forbid();
        return access.CompanyIds.Contains(companyId) ? null : NotFound();
    }

    private Task<LegalEntity?> LoadEditableCompanyAsync(int companyId, CancellationToken ct)
        => _db.LegalEntities.AsNoTracking().Where(row => row.Id == companyId)
            .Select(row => new LegalEntity { Id = row.Id, ProfileStatus = row.ProfileStatus, BankAccountsJson = row.BankAccountsJson })
            .SingleOrDefaultAsync(ct);

    private async Task<ActionResult<CompanyBranchDto>> MutateAsync(int companyId, Func<Task<ActionResult<CompanyBranchDto>>> action, CancellationToken ct)
    {
        try
        {
            var denied = await CheckAccessAsync(companyId, true, ct);
            if (denied is not null) return denied;
            return await DocumentSellerLease.RunSerializableAsync(_db, async lease =>
            {
                lease.AdmitCleanTracker();
                _db.ChangeTracker.Clear();
                // Establish the transaction snapshot from the approved current actor,
                // then recheck own-company membership before touching branch data.
                var currentDenial = await CheckAccessAsync(companyId, true, ct);
                if (currentDenial is not null) return new ActionResult<CompanyBranchDto>(currentDenial);
                await lease.LockRegistryAsync(ct);
                await lease.LockCompanyRowAsync(companyId, ct);
                return await action();
            }, static result => result.Result is CreatedResult or OkObjectResult, ct);
        }
        catch (DbUpdateException error) when (IsDuplicateName(error))
        {
            return Conflict(new { message = "Подразделение с таким названием уже есть у компании." });
        }
        catch (Exception error) when (error is SellerSnapshotConflictException || DocumentSellerLease.IsConflict(error))
        {
            return Conflict(new { code = "CompanyBranchConflict", message = "Компания или подразделение изменились либо сейчас сохраняются. Изменения не выполнены. Обновите подразделения и повторите действие." });
        }
    }

    private static bool IsDuplicateName(DbUpdateException error) => error.GetBaseException() is
        Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_CompanyBranches_LegalEntityId_Name" }
        || error.GetBaseException() is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 } sqlite
        && sqlite.Message.Contains("CompanyBranches.LegalEntityId, CompanyBranches.Name", StringComparison.Ordinal);

    private async Task<ActionResult?> ApplyAsync(LegalEntity company, CompanyBranch branch, CompanyBranchRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return BadRequest(new { message = "Укажите название подразделения." });
        }

        var kind = request.Kind is null ? CompanyBranchKinds.Branch : CompanyBranchKinds.Normalize(request.Kind);
        if (kind is null)
        {
            return BadRequest(new { message = "Неизвестный вид подразделения." });
        }

        var status = request.Status is null ? branch.Status : CompanyProfileStatuses.NormalizeBranch(request.Status);
        if (status is null)
        {
            return BadRequest(new { message = "Неизвестный статус подразделения." });
        }

        var kpp = Clean(request.Kpp);
        if (kpp is not null && !RussianRequisitesValidator.IsValidKpp(kpp))
        {
            return BadRequest(new { message = "КПП подразделения должен состоять из 9 знаков." });
        }

        var bankAccountId = Clean(request.BankAccountId);
        if (bankAccountId is not null && !CompanyRequisitesResolver.TryFindActiveAccount(company, bankAccountId, out _))
        {
            return BadRequest(new { message = "Банковский счёт не найден среди активных счетов этой компании." });
        }

        var signerJson = Clean(request.SignerJson);
        if (signerJson is not null && !IsJsonObject(signerJson))
        {
            return BadRequest(new { message = "Данные подписанта должны быть объектом." });
        }

        var duplicate = await _db.CompanyBranches.AnyAsync(
            b => b.LegalEntityId == company.Id && b.Id != branch.Id && b.Name == name, ct);
        if (duplicate)
        {
            return Conflict(new { message = "Подразделение с таким названием уже есть у компании." });
        }

        branch.Kind = kind;
        branch.Name = name;
        branch.Code = Clean(request.Code);
        branch.Status = status;
        branch.Kpp = kpp;
        branch.Address = Clean(request.Address);
        branch.BankAccountId = bankAccountId;
        branch.SignerJson = signerJson;
        branch.Notes = Clean(request.Notes);
        return null;
    }

    private static bool IsJsonObject(string value)
    {
        if (value.Length > MaxSignerJsonLength)
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(value) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
