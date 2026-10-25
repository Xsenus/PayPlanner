using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

/// <summary>Uses fresh section permissions and existing employee-company assignments.</summary>
public sealed record CompanyProfileAccess(SectionAccessContext Section, HashSet<int> CompanyIds)
{
    public bool CanEdit => Section.Permission?.CanEdit == true;

    public static async Task<CompanyProfileAccess?> ResolveAsync(PaymentContext db, ClaimsPrincipal? user, CancellationToken ct)
    {
        var section = await db.ResolveSectionAccessAsync(user, RolePermissionCatalog.CompanyProfile, ct, requireApprovedUser: true);
        // Legacy roles without explicit permissions never inherit access to the company profile.
        if (section?.Permission?.CanView != true) return null;

        var companies = await OwnCompanyRegistry.LoadIdsAsync(db, ct);
        if (!section.Visibility.IsAdmin)
        {
            var assigned = await db.UserCompanyAccesses.AsNoTracking()
                .Where(item => item.UserId == section.Visibility.UserId && companies.Contains(item.LegalEntityId))
                .Select(item => item.LegalEntityId).ToListAsync(ct);
            companies.IntersectWith(assigned);
        }
        return new CompanyProfileAccess(section, companies);
    }
}
