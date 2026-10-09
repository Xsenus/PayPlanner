using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Controllers;

/// <summary>
/// Филиалы, офисы, точки и направления собственной компании. Управление — только у администратора,
/// как и остальные настройки собственных компаний. Филиал не имеет своего ИНН: переопределяются
/// только КПП, адрес, банковский счёт и подписант, всё остальное наследуется от компании.
/// </summary>
[ApiController]
[Route("api/legal-entities/{companyId:int}/branches")]
[Authorize(Policy = "Admin")]
public class CompanyBranchesController : ControllerBase, IAsyncActionFilter
{
    private const int MaxSignerJsonLength = 8000;

    private readonly PaymentContext _db;

    public CompanyBranchesController(PaymentContext db)
    {
        _db = db;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet]
    public async Task<ActionResult<List<CompanyBranchDto>>> List(
        int companyId,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
    {
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
    public async Task<ActionResult<CompanyBranchDto>> Create(int companyId, [FromBody] CompanyBranchRequest request, CancellationToken ct)
    {
        var company = await _db.LegalEntities.FirstOrDefaultAsync(le => le.Id == companyId, ct);
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
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Параллельное создание подразделения с тем же названием: уникальный индекс сработал раньше проверки.
            return Conflict(new { message = "Подразделение с таким названием уже есть у компании." });
        }

        return Created($"/api/legal-entities/{companyId}/branches/{branch.Id}", CompanyBranchDto.From(branch));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CompanyBranchDto>> Update(
        int companyId,
        int id,
        [FromBody] CompanyBranchRequest request,
        CancellationToken ct)
    {
        var branch = await _db.CompanyBranches
            .Include(b => b.LegalEntity)
            .FirstOrDefaultAsync(b => b.Id == id && b.LegalEntityId == companyId, ct);
        if (branch is null)
        {
            return NotFound();
        }

        var error = await ApplyAsync(branch.LegalEntity, branch, request, ct);
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
        var branch = await _db.CompanyBranches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id && b.LegalEntityId == companyId, ct);
        if (branch is null)
        {
            return NotFound();
        }

        var company = await _db.LegalEntities
            .AsNoTracking()
            .Include(le => le.Addresses)
            .FirstAsync(le => le.Id == companyId, ct);

        return Ok(new CompanyEffectiveRequisitesResponse(
            CompanyRequisitesResolver.Resolve(company, branch, DateTime.UtcNow)));
    }

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
