using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData(nameof(LegalEntityRequest.LogoDataUrl))]
    [InlineData(nameof(LegalEntityRequest.SealDataUrl))]
    [InlineData(nameof(LegalEntityRequest.DirectorSignatureDataUrl))]
    public async Task Create_RejectsNewDamagedImageWithoutInsertingAnyCompany(string field)
    {
        await using var db = await CreateContextAsync();
        var request = BuildRequest();
        typeof(LegalEntityRequest).GetProperty(field)!.SetValue(request, "data:image/png;base64,QUFBQUFB");

        var action = await CreateController(db, 10).Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToListAsync());
        Assert.Empty(await db.CompanyBrandImageVersions.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_NewImagesAreValidatedAtomicallyRegardlessOfChangedFlag(bool? changed)
    {
        await using var db = await CreateContextAsync();
        var company = new LegalEntity { ShortName = "До изменения", LogoDataUrl = "data:image/png;base64,AAAA", SealDataUrl = "data:image/webp;base64,QUFBQUFB" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        var auditCount = await db.ObjectAuditEvents.CountAsync();
        var controller = CreateController(db, 10);
        var request = new LegalEntityRequest
        {
            ShortName = "Не сохранять при отказе",
            LogoDataUrl = CompanyImageTestData.TransparentPng,
            SealDataUrl = CompanyImageTestData.OpaquePng,
            SealChanged = changed,
        };

        var failed = await controller.Update(company.Id, request, CancellationToken.None);

        Assert.Contains("Печать компании", Assert.IsType<string>(Assert.IsType<BadRequestObjectResult>(failed.Result).Value));
        var unchanged = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal("До изменения", unchanged.ShortName);
        Assert.Equal("data:image/png;base64,AAAA", unchanged.LogoDataUrl);
        Assert.Equal("data:image/webp;base64,QUFBQUFB", unchanged.SealDataUrl);
        Assert.Equal(auditCount, await db.ObjectAuditEvents.CountAsync());

        request.SealDataUrl = CompanyImageTestData.TransparentPng;
        var success = await controller.Update(company.Id, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(success.Result);
        var stored = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal(request.ShortName, stored.ShortName);
        Assert.Equal(request.LogoDataUrl, stored.LogoDataUrl);
        Assert.Equal(request.SealDataUrl, stored.SealDataUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_UnchangedLegacyImageDoesNotNeedToMeetNewUploadRules(bool? changed)
    {
        await using var db = await CreateContextAsync();
        const string legacy = "data:image/webp;base64,QUFBQUFB";
        var company = new LegalEntity { ShortName = "Старый профиль", SealDataUrl = legacy };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        var result = await CreateController(db, 10).Update(company.Id,
            new LegalEntityRequest { ShortName = "Новые реквизиты", SealDataUrl = legacy, SealChanged = changed }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal("Новые реквизиты", stored.ShortName);
        Assert.Equal(legacy, stored.SealDataUrl);
    }

    [Fact]
    public async Task Update_NewSealLeavesIssuedDocumentAndItsImageVersionUnchanged()
    {
        await using var db = await CreateContextAsync();
        var adminId = await CreateUserAsync(db, roleName: "admin");
        const string legacy = "data:image/png;base64,QUFBQUFB";
        var company = new LegalEntity { ShortName = "Продавец", SealDataUrl = legacy };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = company.Id.ToString() });
        var act = new Act { Number = "Выпущенный акт", SellerCompanyId = company.Id };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var seller = new DocumentSellerService(db);
        await seller.ResolveAsync(act, company, freeze: true, CancellationToken.None);
        await db.SaveChangesAsync();
        var snapshot = act.SellerSnapshotJson;
        Assert.NotNull(snapshot);
        var version = await db.CompanyBrandImageVersions.AsNoTracking().SingleAsync();
        Assert.Equal(legacy, version.DataUrl);

        var result = await CreateController(db, adminId).Update(company.Id,
            new LegalEntityRequest { ShortName = company.ShortName, SealDataUrl = CompanyImageTestData.TransparentPng, SealChanged = true }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(snapshot, (await db.Acts.AsNoTracking().SingleAsync()).SellerSnapshotJson);
        Assert.Equal(legacy, (await db.CompanyBrandImageVersions.AsNoTracking().SingleAsync()).DataUrl);
        Assert.Equal(CompanyImageTestData.TransparentPng, (await db.LegalEntities.AsNoTracking().SingleAsync()).SealDataUrl);
        Assert.Equal(legacy, (await seller.ResolveAsync(act, company, freeze: false, CancellationToken.None))!.SealDataUrl);
    }

    [Fact]
    public async Task Update_ImageContentIsNotInspectedBeforeCompanyProfileAccess()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = (await db.Users.SingleAsync(user => user.Id == userId)).RoleId;
        var own = await CreateLegalEntityAsync(db, "Назначенная");
        var other = await CreateLegalEntityAsync(db, "Чужая");
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{own},{other}]" });
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = true });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = own });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdmin: false);
        var request = new LegalEntityRequest { ShortName = "Название", SealDataUrl = CompanyImageTestData.OpaquePng };

        var foreign = await controller.Update(other, request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(foreign.Result);
        var allowed = await controller.Update(own, request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(allowed.Result);
        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == roleId && item.Section == RolePermissionCatalog.CompanyProfile);
        permission.CanEdit = false;
        await db.SaveChangesAsync();
        var revoked = await controller.Update(own, request, CancellationToken.None);
        Assert.IsType<ForbidResult>(revoked.Result);
        Assert.Null((await db.LegalEntities.AsNoTracking().SingleAsync(item => item.Id == own)).SealDataUrl);
    }
}
