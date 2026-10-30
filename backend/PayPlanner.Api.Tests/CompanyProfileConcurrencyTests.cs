using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    // Capture this at fixture/form load, never silently refresh a submitted request.
    private static async Task<string> ReadProfileEditVersionAsync(PaymentContext db, int id)
    {
        var entity = await db.LegalEntities.AsNoTracking().Include(row => row.Clients).Include(row => row.Addresses).SingleAsync(row => row.Id == id);
        var defaults = await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(id);
        return CompanyProfileEditVersion.Compute(entity, entity.Addresses, entity.Clients.Select(client => client.Id), defaults.Version!);
    }

    private static async Task<(LegalEntitiesController Controller, int Id)> ConcurrentProfileFixture(PaymentContext db)
    {
        var userId = await CreateUserAsync(db, "admin");
        var id = await CreateLegalEntityAsync(db, "Our company");
        (await db.LegalEntities.SingleAsync(row => row.Id == id)).Phone = "000";
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{id}]" });
        await db.SaveChangesAsync();
        return (CreateController(db, userId), id);
    }

    private static async Task<LegalEntityDetailDto> ReadConcurrentProfile(LegalEntitiesController controller, int id)
        => Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetById(id, default)).Result).Value);

    private static LegalEntityRequest ConcurrentProfileRequest(LegalEntityDetailDto dto) => new()
    {
        ShortName = dto.ShortName,
        Phone = dto.Phone,
        FullName = dto.FullName,
        ProfileEditVersion = dto.ProfileEditVersion,
        ClientIds = dto.Clients.Select(client => client.Id).ToList(),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task ProfileEdit_RequiresMatchingVersionWithoutDisclosingCurrentVersion(string? version)
    {
        await using var db = await CreateContextAsync();
        var (controller, id) = await ConcurrentProfileFixture(db);
        var current = await ReadConcurrentProfile(controller, id);
        var request = ConcurrentProfileRequest(current); request.ProfileEditVersion = version; request.Phone = "Must not save";

        var conflict = Assert.IsType<ConflictObjectResult>((await controller.Update(id, request, default)).Result);

        Assert.DoesNotContain(current.ProfileEditVersion!, System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("000", (await db.LegalEntities.AsNoTracking().SingleAsync()).Phone);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileEdit_StaleFormCannotOverwriteRequisitesEvenWithValidImageVersion(bool editImages)
    {
        await using var db = await CreateContextAsync();
        var (controller, id) = await ConcurrentProfileFixture(db);
        var first = await ReadConcurrentProfile(controller, id);
        var second = await ReadConcurrentProfile(controller, id);
        Assert.Equal(first.ProfileEditVersion, second.ProfileEditVersion);
        var winner = ConcurrentProfileRequest(first); winner.Phone = "111";
        var saved = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.Update(id, winner, default)).Result).Value);
        Assert.NotEqual(first.ProfileEditVersion, saved.ProfileEditVersion);
        Assert.Equal(second.DocumentImagePrintDefaultsVersion, saved.DocumentImagePrintDefaultsVersion);
        var stale = ConcurrentProfileRequest(second); stale.FullName = "Stale overwrite";
        stale.DocumentImagePrintDefaultsChanged = editImages;
        stale.DocumentImagePrintDefaultsVersion = second.DocumentImagePrintDefaultsVersion;

        Assert.IsType<ConflictObjectResult>((await controller.Update(id, stale, default)).Result);

        var stored = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal("111", stored.Phone); Assert.Equal(first.FullName, stored.FullName);
        var reviewed = ConcurrentProfileRequest(await ReadConcurrentProfile(controller, id)); reviewed.FullName = "Reviewed edit";
        Assert.IsType<OkObjectResult>((await controller.Update(id, reviewed, default)).Result);
        stored = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal("111", stored.Phone); Assert.Equal("Reviewed edit", stored.FullName);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("address")]
    [InlineData("image")]
    [InlineData("defaults")]
    public async Task ProfileEdit_ChildAndImageChangesInvalidateVersionWithoutUpdatedAtChange(string change)
    {
        await using var db = await CreateContextAsync();
        var (controller, id) = await ConcurrentProfileFixture(db);
        var opened = await ReadConcurrentProfile(controller, id);
        if (change == "link") db.Clients.Add(new Client { Name = "Newly linked", LegalEntityId = id });
        if (change == "address") db.AddressRecords.Add(new AddressRecord { LegalEntityId = id, Value = "New address" });
        if (change == "image") await db.LegalEntities.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LogoDataUrl, CompanyImageTestData.TransparentPng));
        if (change == "defaults") await new CompanyDocumentImagePrintDefaultsService(db).StageAsync(id, new(new(true, false, false), null, null), true);
        await db.SaveChangesAsync();
        Assert.Equal(opened.UpdatedAt, (await db.LegalEntities.AsNoTracking().SingleAsync()).UpdatedAt);
        var changed = await ReadProfileEditVersionAsync(db, id);
        Assert.NotEqual(opened.ProfileEditVersion, changed);

        Assert.IsType<ConflictObjectResult>((await controller.Update(id, ConcurrentProfileRequest(opened), default)).Result);

        Assert.Equal(changed, await ReadProfileEditVersionAsync(db, id));
    }

    [Fact]
    public async Task ProfileEdit_InvalidRequestRollsBackAndReleasesItsTransaction()
    {
        await using var db = await CreateContextAsync();
        var (controller, id) = await ConcurrentProfileFixture(db);
        var opened = await ReadConcurrentProfile(controller, id);
        var invalid = ConcurrentProfileRequest(opened); invalid.ShortName = " ";
        Assert.IsType<BadRequestObjectResult>((await controller.Update(id, invalid, default)).Result);
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(opened.ProfileEditVersion, await ReadProfileEditVersionAsync(db, id));
        var corrected = ConcurrentProfileRequest(opened); corrected.Phone = "222";
        Assert.IsType<OkObjectResult>((await controller.Update(id, corrected, default)).Result);
    }

    [Fact]
    public async Task ProfileEdit_FailureAfterSaveRollsBackProfileLinksAndDefaults()
    {
        var failure = new ProfileEditAfterSaveFailure();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var (controller, id) = await ConcurrentProfileFixture(db);
        var client = new Client { Name = "Unlinked" }; db.Clients.Add(client); await db.SaveChangesAsync();
        var opened = await ReadConcurrentProfile(controller, id);
        var request = ConcurrentProfileRequest(opened); request.Phone = "Must rollback"; request.ClientIds = [client.Id];
        request.Addresses = [new() { Value = "Must rollback address" }];
        request.DocumentImagePrintDefaults = new() { Invoice = new() { IncludeLogo = true, IncludeSeal = false, IncludeSignature = false } };
        request.DocumentImagePrintDefaultsChanged = true; request.DocumentImagePrintDefaultsVersion = opened.DocumentImagePrintDefaultsVersion;
        failure.Enabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Update(id, request, default));

        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(opened.ProfileEditVersion, await ReadProfileEditVersionAsync(db, id));
        Assert.Null((await db.Clients.AsNoTracking().SingleAsync()).LegalEntityId);
        Assert.Empty(await db.AddressRecords.AsNoTracking().ToArrayAsync());
        Assert.Null((await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(id)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileEdit_RejectsForeignPendingWritesAndAmbientTransactionWithoutOwningThem(bool ambient)
    {
        await using var db = await CreateContextAsync();
        var (controller, id) = await ConcurrentProfileFixture(db);
        var opened = await ReadConcurrentProfile(controller, id);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var unrelated = new Client { Name = "Caller owns this" }; db.Clients.Add(unrelated);

        Assert.IsType<ConflictObjectResult>((await controller.Update(id, ConcurrentProfileRequest(opened), default)).Result);

        Assert.Equal(EntityState.Added, db.Entry(unrelated).State);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal("000", (await db.LegalEntities.AsNoTracking().SingleAsync()).Phone);
    }

    private sealed class ProfileEditAfterSaveFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
            => Enabled ? throw new InvalidOperationException("Synthetic failure after database write") : ValueTask.FromResult(result);
    }
}
