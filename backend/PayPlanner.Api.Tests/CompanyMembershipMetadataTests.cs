using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipMetadata_DetailAndProfileUseAlreadyAuthorizedClassificationWithoutWriting(bool own)
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, own);
        var before = await CompanyDeleteSnapshot(db); var controller = CreateController(db, fixture.Actor);
        var detail = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetById(fixture.Company, default)).Result).Value);
        Assert.Equal(own, detail.IsOwnCompany);
        if (own)
        {
            var profile = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetCompanyProfile(default)).Result).Value);
            Assert.True(profile.IsOwnCompany); Assert.Equal(fixture.Company, profile.Id);
        }
        Assert.Equal(before, await CompanyDeleteSnapshot(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipMetadata_UpdateReturnsCurrentClassification(bool own)
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, own);
        var controller = CreateController(db, fixture.Actor);
        var saved = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.Update(fixture.Company,
            new LegalEntityRequest { ShortName = "Updated requisites", ProfileEditVersion = await ReadProfileEditVersionAsync(db, fixture.Company), ClientIds = [fixture.Client] }, default)).Result).Value);
        Assert.Equal(own, saved.IsOwnCompany); Assert.Equal("Updated requisites", saved.ShortName);
    }

    [Fact]
    public async Task MembershipMetadata_NewCounterpartyIsNotImplicitlyRegisteredAsOwnCompany()
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, true);
        var saved = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<CreatedResult>((await CreateController(db, fixture.Actor).Create(
            new LegalEntityRequest { ShortName = "New counterparty" }, default)).Result).Value);
        Assert.False(saved.IsOwnCompany); Assert.NotEqual(fixture.Company, saved.Id);
    }

    [Fact]
    public async Task MembershipMetadata_OrdinaryEditorDoesNotNeedCompanyProfilePermission()
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, true);
        var counterparty = await CreateLegalEntityAsync(db, "Ordinary visible counterparty");
        var user = await db.Users.SingleAsync(row => row.Id == fixture.Staff);
        db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.LegalEntities, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var controller = CreateController(db, fixture.Staff, isAdmin: false);
        Assert.IsType<ForbidResult>((await controller.GetMainCompanySettings(default)).Result);
        var detail = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetById(counterparty, default)).Result).Value);
        Assert.False(detail.IsOwnCompany);
        Assert.IsType<NotFoundResult>((await controller.GetById(fixture.Company, default)).Result);
    }
}
