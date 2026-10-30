using System.Data.Common;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData("admin", true, true)]
    [InlineData("ADMIN", true, true)]
    [InlineData("editor", false, true)]
    [InlineData("viewer", false, false)]
    [InlineData("no-view", false, false)]
    [InlineData("missing-section", false, false)]
    [InlineData("legacy", false, true)]
    [InlineData("unapproved", false, false)]
    [InlineData("inactive", false, false)]
    [InlineData("missing-user", false, false)]
    [InlineData("empty-database", false, false)]
    [InlineData("missing-id", false, false)]
    [InlineData("anonymous", false, false)]
    public async Task CompanyApproval_StrictGuardsUseFreshApprovedActorInOneQuery(string mode, bool expectedAdmin, bool expectedCreate)
    {
        var counter = new CompanyApprovalQueryCounter();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: counter);
        var id = 99;
        if (mode != "empty-database")
        {
            id = await CreateUserAsync(db, mode is "admin" or "ADMIN" ? mode : "ordinary");
            if (mode is not ("admin" or "ADMIN" or "legacy"))
            {
                var roleId = (await db.Users.SingleAsync(user => user.Id == id)).RoleId;
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    Section = mode == "missing-section" ? RolePermissionCatalog.Clients : RolePermissionCatalog.LegalEntities,
                    CanView = mode != "no-view",
                    CanCreate = mode != "viewer",
                    ViewScope = DataVisibilityScope.Own,
                });
                await db.SaveChangesAsync();
            }
            if (mode == "unapproved") await db.Users.Where(user => user.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsApproved, false));
            if (mode == "inactive") await db.Users.Where(user => user.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
            if (mode == "missing-user") id = 999;
        }
        var claims = new List<Claim> { new(ClaimTypes.Role, "admin") };
        if (mode != "missing-id") claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, mode == "anonymous" ? null : "Fixture"));
        counter.Enabled = true;
        Assert.Equal(expectedCreate, await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.LegalEntities, permission => permission.CanCreate));
        Assert.Equal(mode is "missing-id" or "anonymous" ? 0 : 1, counter.Commands);
        counter.Commands = 0;
        Assert.Equal(expectedAdmin, await db.IsApprovedUserAdminAsync(actor));
        Assert.Equal(mode is "missing-id" or "anonymous" ? 0 : 1, counter.Commands);
        counter.Enabled = false;
        if (mode is "unapproved" or "empty-database")
        {
            // The pre-existing generic helper keeps its explicit legacy contract;
            // company writes opt into the strict method instead of changing every API.
            Assert.True(await db.HasUserSectionPermissionAsync(actor, RolePermissionCatalog.LegalEntities, permission => permission.CanCreate));
        }
    }

    private sealed class CompanyApprovalQueryCounter : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Commands { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Commands++;
            return ValueTask.FromResult(result);
        }
    }
}
