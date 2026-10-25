using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CompanyProfilePostgreSqlTests(PostgreSqlMigratedTemplate databases) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData("permission")]
    [InlineData("company")]
    [InlineData("active")]
    [InlineData("approval")]
    public async Task FreshRoleAndCompanyBoundary_AreEnforcedOnPostgreSql(string revoke)
    {
        await using var lease = await databases.CreateDatabaseAsync("pp_profile_access_");
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var role = new Role { Name = "profile-reader" };
        var user = new User { Email = "reader@fixture.local", FullName = "Synthetic reader", PasswordHash = "unused", Role = role, IsActive = true, IsApproved = true, IsEmployee = true };
        var first = new LegalEntity { ShortName = "Assigned company" };
        var second = new LegalEntity { ShortName = "Foreign company" };
        db.Users.Add(user); db.LegalEntities.AddRange(first, second); await db.SaveChangesAsync();
        var permission = new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All };
        var assignment = new UserCompanyAccess { UserId = user.Id, LegalEntityId = first.Id };
        db.RolePermissions.Add(permission); db.UserCompanyAccesses.Add(assignment);
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{first.Id},{second.Id}]" }); await db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin")], "fixture"));
        var allowed = await CompanyProfileAccess.ResolveAsync(db, principal, CancellationToken.None);
        Assert.NotNull(allowed); Assert.True(allowed.CanEdit); Assert.Equal(new[] { first.Id }, allowed.CompanyIds);
        if (revoke == "permission") permission.CanView = false;
        if (revoke == "company") db.UserCompanyAccesses.Remove(assignment);
        if (revoke == "active") user.IsActive = false;
        if (revoke == "approval") user.IsApproved = false;
        await db.SaveChangesAsync();
        var denied = await CompanyProfileAccess.ResolveAsync(db, principal, CancellationToken.None);
        if (revoke == "company") { Assert.NotNull(denied); Assert.Empty(denied.CompanyIds); }
        else Assert.Null(denied);
        Assert.Equal(2, await db.LegalEntities.CountAsync());
    }
}
