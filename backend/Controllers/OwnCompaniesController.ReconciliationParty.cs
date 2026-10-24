using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Controllers;

public sealed record OwnCompanyReconciliationParty(
    int Id, string ShortName, string? FullName, string? Inn, string? Kpp, string? Ogrn,
    string? Address, string? Director, string? OrganizationForm, string? TreasuryBankName,
    string? TreasuryBankBik, string? TreasuryAccountNumber, string? DirectorSignatureDataUrl, string? SealDataUrl);

public partial class OwnCompaniesController
{
    // A permitted document needs its issuer requisites, not access to the private company profile.
    [HttpGet("reconciliation-party")]
    public async Task<ActionResult<OwnCompanyReconciliationParty>> GetReconciliationParty([FromQuery] int clientId, CancellationToken ct)
    {
        if (clientId <= 0) return BadRequest();
        var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
        if (access?.Permission?.CanView != true) return Forbid();
        if (!access.Visibility.IsAdmin && !await _db.RolePermissions.AsNoTracking().AnyAsync(permission =>
            permission.Role!.Users.Any(user => user.Id == access.Visibility.UserId && user.IsActive && user.IsApproved)
            && (permission.Section == RolePermissionCatalog.Accounts || permission.Section == RolePermissionCatalog.Acts)
            && permission.CanView, ct)) return Forbid();
        if (!await _db.Clients.AsNoTracking().ApplyClientVisibility(access.Visibility).AnyAsync(client => client.Id == clientId, ct)) return NotFound();

        var ids = await OwnCompanyRegistry.LoadIdsAsync(_db, ct);
        var defaultValue = await _db.SystemSettings.AsNoTracking().Where(item => item.Key == DefaultMainCompanyIdSettingKey || item.Key == LegacyMainCompanySettingKey)
            .OrderBy(item => item.Key == DefaultMainCompanyIdSettingKey ? 0 : 1).Select(item => item.Value).FirstOrDefaultAsync(ct);
        var companyId = int.TryParse(defaultValue, out var configuredId) && ids.Contains(configuredId)
            ? configuredId : ids.OrderBy(id => id).FirstOrDefault();
        var party = await _db.LegalEntities.AsNoTracking()
            .Where(company => company.Id == companyId && company.ProfileStatus == CompanyProfileStatuses.Active)
            .Select(company => new OwnCompanyReconciliationParty(company.Id, company.ShortName, company.FullName,
                company.Inn, company.Kpp, company.Ogrn, company.Address, company.Director, company.OrganizationForm,
                company.TreasuryBankName, company.TreasuryBankBik, company.TreasuryAccountNumber,
                company.DirectorSignatureDataUrl, company.SealDataUrl)).FirstOrDefaultAsync(ct);
        return party is null ? NotFound() : Ok(party);
    }
}
