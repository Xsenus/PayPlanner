using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

public sealed record OwnCompanyBranchOption(int Id, string Name, string Kind);

public sealed record OwnCompanyOption(int Id, string ShortName, bool IsDefault, IReadOnlyList<OwnCompanyBranchOption> Branches);

/// <summary>
/// Короткий список собственных компаний и их подразделений для выбора продавца в счёте или акте.
/// Отдаёт только названия и идентификаторы активных записей, поэтому доступен любому авторизованному
/// сотруднику; право менять продавца проверяют методы счетов и актов.
/// </summary>
[ApiController]
[Route("api/own-companies")]
[Authorize]
public partial class OwnCompaniesController : ControllerBase
{
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string DefaultMainCompanyIdSettingKey = "legalEntities.defaultMainCompanyId";
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";

    private readonly PaymentContext _db;

    public OwnCompaniesController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet("options")]
    public async Task<ActionResult<List<OwnCompanyOption>>> GetOptions(CancellationToken ct)
    {
        var values = await _db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == MainCompanyIdsSettingKey
                || item.Key == DefaultMainCompanyIdSettingKey
                || item.Key == LegacyMainCompanySettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var ids = new List<int>();
        if (values.TryGetValue(MainCompanyIdsSettingKey, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                ids.AddRange((JsonSerializer.Deserialize<List<int>>(raw) ?? []).Where(id => id > 0));
            }
            catch (JsonException)
            {
                // Некорректное значение настройки игнорируется, как и в остальных местах.
            }
        }

        if (ids.Count == 0
            && values.TryGetValue(LegacyMainCompanySettingKey, out var legacy)
            && int.TryParse(legacy, out var legacyId)
            && legacyId > 0)
        {
            ids.Add(legacyId);
        }

        if (ids.Count == 0)
        {
            return Ok(new List<OwnCompanyOption>());
        }

        int? defaultId = values.TryGetValue(DefaultMainCompanyIdSettingKey, out var rawDefault)
            && int.TryParse(rawDefault, out var parsedDefault) && ids.Contains(parsedDefault)
            ? parsedDefault
            : ids[0];

        var companies = await _db.LegalEntities
            .AsNoTracking()
            .Where(le => ids.Contains(le.Id) && le.ProfileStatus == CompanyProfileStatuses.Active)
            .OrderBy(le => le.ShortName)
            .Select(le => new { le.Id, le.ShortName })
            .ToListAsync(ct);

        var branches = await _db.CompanyBranches
            .AsNoTracking()
            .Where(b => ids.Contains(b.LegalEntityId) && b.Status == CompanyProfileStatuses.Active)
            .OrderBy(b => b.Name)
            .Select(b => new { b.LegalEntityId, b.Id, b.Name, b.Kind })
            .ToListAsync(ct);

        var byCompany = branches
            .GroupBy(b => b.LegalEntityId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<OwnCompanyBranchOption>)g
                .Select(b => new OwnCompanyBranchOption(b.Id, b.Name, b.Kind))
                .ToList());

        return Ok(companies
            .Select(c => new OwnCompanyOption(
                c.Id,
                c.ShortName,
                c.Id == defaultId,
                byCompany.TryGetValue(c.Id, out var list) ? list : []))
            .ToList());
    }
}
