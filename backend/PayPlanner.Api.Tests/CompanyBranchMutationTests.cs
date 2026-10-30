using System.Data.Common;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
    private static CompanyBranchesController BranchController(PaymentContext db, int actor)
        => new(db) { ControllerContext = CreateController(db, actor).ControllerContext };

    private static async Task<ActionResult<CompanyBranchDto>> BranchMutation(PaymentContext db, CompanyDeleteData fixture, string operation)
    {
        var controller = BranchController(db, fixture.Actor);
        var request = new CompanyBranchRequest { Name = "Branch write", Kpp = "780201001" };
        if (operation == "update") request.ExpectedVersion = CompanyProfileEditVersion.Compute(await db.CompanyBranches.AsNoTracking().SingleAsync(row => row.Id == fixture.Branch));
        return operation == "create" ? await controller.Create(fixture.Company, request, default) : await controller.Update(fixture.Company, fixture.Branch, request, default);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("update", false)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    public async Task BranchMutation_DoesNotOwnOrSaveTheCallersUnitOfWork(string operation, bool ambient)
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, true);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var pending = new Client { Name = "Unrelated pending client" }; db.Clients.Add(pending);
        var before = await CompanyDeleteSnapshot(db);
        Assert.IsType<ConflictObjectResult>((await BranchMutation(db, fixture, operation)).Result);
        Assert.Same(transaction, db.Database.CurrentTransaction); Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.Equal(before, await CompanyDeleteSnapshot(db));
    }

    [Theory]
    [InlineData("create", "fault")]
    [InlineData("update", "fault")]
    [InlineData("create", "cancel")]
    [InlineData("update", "cancel")]
    [InlineData("create", "duplicate")]
    [InlineData("update", "other-unique")]
    public async Task BranchMutation_FailureAfterSaveRollsBackEveryPersistedValue(string operation, string kind)
    {
        var failure = new BranchSaveFailure(kind);
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await CompanyDeleteFixture(db, true); var before = await CompanyDeleteSnapshot(db);
        failure.Enabled = true;
        if (kind == "duplicate") Assert.IsType<ConflictObjectResult>((await BranchMutation(db, fixture, operation)).Result);
        else if (kind == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => BranchMutation(db, fixture, operation));
        else if (kind == "other-unique") await Assert.ThrowsAsync<DbUpdateException>(() => BranchMutation(db, fixture, operation));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => BranchMutation(db, fixture, operation));
        Assert.Equal(1, failure.Hits); Assert.Equal(before, await CompanyDeleteSnapshot(db));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class BranchSaveFailure(string kind) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public int Hits { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return ValueTask.FromResult(result);
            Enabled = false; Hits++;
            if (kind == "cancel") throw new OperationCanceledException("Synthetic branch cancellation after save");
            if (kind is "duplicate" or "other-unique")
                throw new DbUpdateException("Synthetic constraint failure after save", new SqliteException(kind == "duplicate"
                    ? "UNIQUE constraint failed: CompanyBranches.LegalEntityId, CompanyBranches.Name" : "UNIQUE constraint failed: ObjectAuditEvents.Id", 19, 2067));
            throw new InvalidOperationException("Synthetic branch fault after save");
        }
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("claim-only")]
    [InlineData("unknown-user")]
    public async Task BranchAccess_DirectCallsDoNotInheritLegacyAdminClaims(string kind)
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, true);
        var controller = BranchController(db, int.MaxValue);
        if (kind != "unknown-user") controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(kind == "anonymous" ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Test")) }
        };
        var before = await CompanyDeleteSnapshot(db);
        Assert.IsType<ForbidResult>((await controller.List(fixture.Company, true, default)).Result);
        Assert.IsType<ForbidResult>((await controller.GetEffectiveRequisites(fixture.Company, fixture.Branch, default)).Result);
        Assert.IsType<ForbidResult>((await controller.Create(fixture.Company, new CompanyBranchRequest { Name = "Must not create" }, default)).Result);
        Assert.IsType<ForbidResult>((await controller.Update(fixture.Company, fixture.Branch, new CompanyBranchRequest { Name = "Must not update" }, default)).Result);
        Assert.Equal(before, await CompanyDeleteSnapshot(db));
    }

    [Fact]
    public async Task BranchAccess_EffectiveRequisitesUseAllInheritedFieldsWithoutLoadingImagesOrWriting()
    {
        var capture = new BranchQueryCapture();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: capture);
        var fixture = await CompanyDeleteFixture(db, true);
        var company = await db.LegalEntities.SingleAsync(row => row.Id == fixture.Company);
        company.FullName = "Full source name"; company.Inn = "7707083893"; company.Kpp = "770701001"; company.Ogrn = "1027700132195";
        company.TreasuryBankName = "Treasury bank"; company.TreasuryBankBik = "044525225"; company.TreasuryAccountNumber = "Treasury account"; company.TreasuryCorrAccountNumber = "Treasury correspondent";
        company.DocumentDirectorJson = "{\"lastName\":\"Inherited signer\"}"; company.LogoDataUrl = CompanyImageTestData.TransparentPng;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await CompanyDeleteSnapshot(db); capture.Commands.Clear();
        var controller = BranchController(db, fixture.Actor);
        var response = Assert.IsType<CompanyEffectiveRequisitesResponse>(Assert.IsType<OkObjectResult>((await controller.GetEffectiveRequisites(fixture.Company, fixture.Branch, default)).Result).Value).Requisites;
        Assert.Equal("Full source name", response.FullName); Assert.Equal(company.Inn, response.Inn); Assert.Equal(company.Kpp, response.Kpp); Assert.Equal(company.Ogrn, response.Ogrn);
        Assert.Equal("Historical address", response.Address); Assert.Equal(company.TreasuryBankName, response.BankName); Assert.Equal(company.TreasuryBankBik, response.Bik);
        Assert.Equal(company.TreasuryAccountNumber, response.AccountNumber); Assert.Equal(company.TreasuryCorrAccountNumber, response.CorrAccountNumber); Assert.Equal(company.DocumentDirectorJson, response.SignerJson);
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("LogoDataUrl", StringComparison.Ordinal) || sql.Contains("SealDataUrl", StringComparison.Ordinal) || sql.Contains("DirectorSignatureDataUrl", StringComparison.Ordinal));
        Assert.Empty(db.ChangeTracker.Entries()); Assert.Equal(before, await CompanyDeleteSnapshot(db));
        Assert.IsType<NotFoundResult>((await controller.GetEffectiveRequisites(fixture.Other, fixture.Branch, default)).Result);
    }

    private sealed class BranchQueryCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText); return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BranchVersion_PostgreSqlRejectsOldBaselineWithoutChangingDocuments(bool legacyWrite)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var first = CompanyCreatePgContext(database.ConnectionString);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(first, true);
        var protectedBefore = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(first))!.AsObject(); protectedBefore.Remove("branches");
        var controller = BranchController(first, fixture.Actor);
        var created = Assert.IsType<CompanyBranchDto>(Assert.IsType<CreatedResult>((await controller.Create(fixture.Company,
            new CompanyBranchRequest { Name = "Versioned office" }, default)).Result).Value);
        var read = (await second.CompanyBranches.AsNoTracking().SingleAsync(row => row.Id == created.Id));
        Assert.Equal(created.EditVersion, CompanyProfileEditVersion.Compute(read));
        if (legacyWrite)
            await second.CompanyBranches.Where(row => row.Id == created.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Notes, "Direct legacy notes"));
        else
            Assert.IsType<OkObjectResult>((await BranchController(second, fixture.Actor).Update(fixture.Company, created.Id,
                new CompanyBranchRequest { Name = created.Name, ExpectedVersion = created.EditVersion, Notes = "Other editor notes", SignerJson = "{\"name\":\"Other signer\"}" }, default)).Result);
        var beforeConflict = await CompanyDeleteSnapshot(second);
        var conflict = Assert.IsType<ConflictObjectResult>((await controller.Update(fixture.Company, created.Id,
            new CompanyBranchRequest { Name = "Stale office", ExpectedVersion = created.EditVersion, Status = "Archived" }, default)).Result);
        Assert.Contains("CompanyBranchVersionConflict", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(beforeConflict, await CompanyDeleteSnapshot(second));
        Assert.Empty(first.ChangeTracker.Entries()); Assert.Null(first.Database.CurrentTransaction);
        var listed = Assert.IsType<List<CompanyBranchDto>>(Assert.IsType<OkObjectResult>((await controller.List(fixture.Company, true, default)).Result).Value).Single(row => row.Id == created.Id);
        Assert.NotEqual(created.EditVersion, listed.EditVersion);
        Assert.IsType<OkObjectResult>((await controller.Update(fixture.Company, listed.Id,
            new CompanyBranchRequest { Name = listed.Name, ExpectedVersion = listed.EditVersion, Notes = listed.Notes, SignerJson = listed.SignerJson, Status = "Archived" }, default)).Result);
        var final = await second.CompanyBranches.AsNoTracking().SingleAsync(row => row.Id == created.Id);
        Assert.Equal("Archived", final.Status); Assert.Equal(listed.Notes, final.Notes); Assert.Equal(listed.SignerJson, final.SignerJson);
        var protectedAfter = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(second))!.AsObject(); protectedAfter.Remove("branches");
        Assert.Equal(protectedBefore.ToJsonString(), protectedAfter.ToJsonString());
        Assert.All(first.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State)); Assert.Null(first.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("create", "approval", 403)]
    [InlineData("update", "own-company", 404)]
    public async Task BranchMutation_PostgreSqlRechecksAdmissionBeforeWriting(string operation, string change, int expected)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var writer = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(writer, true); var before = await CompanyDeleteProtectedSnapshot(writer);
        string? afterChange = null;
        var hook = new BranchAdmissionHook(async () =>
        {
            if (change == "approval") await writer.Users.Where(row => row.Id == fixture.Actor).ExecuteUpdateAsync(update => update.SetProperty(row => row.IsApproved, false));
            else await writer.SystemSettings.Where(row => row.Key == "legalEntities.mainCompanyIds").ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, $"[{fixture.Other}]"));
            afterChange = await CompanyDeleteSnapshot(writer);
        });
        await using var db = CompanyCreatePgContext(database.ConnectionString, hook);
        var action = (await BranchMutation(db, fixture, operation)).Result;
        Assert.Equal(expected, action is ForbidResult ? 403 : action is NotFoundResult ? 404 : 0);
        Assert.Equal(1, hook.Hits); Assert.Equal(before, await CompanyDeleteProtectedSnapshot(writer));
        Assert.NotNull(afterChange); Assert.Equal(afterChange, await CompanyDeleteSnapshot(writer));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task BranchMutation_PostgreSqlBusyWriterCannotPartiallyChangeAnotherBranch()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(seed, true);
        var protectedBefore = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(seed))!.AsObject(); protectedBefore.Remove("branches");
        var pause = new BranchSavePause();
        await using var first = CompanyCreatePgContext(database.ConnectionString, pause);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        var pending = BranchMutation(first, fixture, "create");
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>((await BranchMutation(second, fixture, "update")).Result);
        }
        finally { pause.Release.TrySetResult(); }
        var created = Assert.IsType<CompanyBranchDto>(Assert.IsType<CreatedResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result).Value);
        Assert.Equal(2, await seed.CompanyBranches.CountAsync());
        Assert.Equal("Historical branch", await seed.CompanyBranches.Where(row => row.Id == fixture.Branch).Select(row => row.Name).SingleAsync());
        Assert.IsType<OkObjectResult>((await BranchController(second, fixture.Actor).Update(fixture.Company, created.Id,
            new CompanyBranchRequest { Name = created.Name, Status = CompanyProfileStatuses.Archived, ExpectedVersion = created.EditVersion }, default)).Result);
        Assert.Equal(CompanyProfileStatuses.Archived, await seed.CompanyBranches.Where(row => row.Id == created.Id).Select(row => row.Status).SingleAsync());
        var protectedAfter = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(seed))!.AsObject(); protectedAfter.Remove("branches");
        Assert.Equal(protectedBefore.ToJsonString(), protectedAfter.ToJsonString());
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
    }

    private sealed class BranchSavePause : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!paused && eventData.Context!.ChangeTracker.Entries<CompanyBranch>().Any())
            {
                paused = true; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task BranchMutation_PostgreSqlFirstFileKeepsTheIssuedBranchRequisites()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(seed, true);
        (await seed.LegalEntities.SingleAsync(row => row.Id == fixture.Company)).Kpp = "770701001";
        var act = await seed.Acts.SingleAsync(); act.SellerSnapshotJson = null; act.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile;
        await seed.SaveChangesAsync(); seed.ChangeTracker.Clear();
        var pause = new BranchFirstFilePause();
        await using var first = CompanyCreatePgContext(database.ConnectionString, pause);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        var document = await first.Acts.AsNoTracking().SingleAsync();
        var pending = new DocumentSellerService(first).ResolveAsync(document, null, true, default, _ => Task.FromResult(true));
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>((await BranchMutation(second, fixture, "update")).Result);
        }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal("770701001", (await pending.WaitAsync(TimeSpan.FromSeconds(20)))!.Kpp);
        var frozen = await seed.Acts.AsNoTracking().SingleAsync(); Assert.NotNull(frozen.SellerSnapshotJson);
        var protectedBefore = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(seed))!.AsObject(); protectedBefore.Remove("branches");
        Assert.IsType<OkObjectResult>((await BranchMutation(second, fixture, "update")).Result);
        Assert.Equal("780201001", await seed.CompanyBranches.Where(row => row.Id == fixture.Branch).Select(row => row.Kpp).SingleAsync());
        Assert.Equal("770701001", (await new DocumentSellerService(seed).ResolveAsync(frozen, null, false, default))!.Kpp);
        var protectedAfter = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(seed))!.AsObject(); protectedAfter.Remove("branches");
        Assert.Equal(protectedBefore.ToJsonString(), protectedAfter.ToJsonString());
    }

    private sealed class BranchFirstFilePause : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!paused && eventData.Context!.ChangeTracker.Entries<Act>().Any(row => !string.IsNullOrWhiteSpace(row.Entity.SellerSnapshotJson)))
            {
                paused = true; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken);
            }
            return result;
        }
    }

    private sealed class BranchAdmissionHook(Func<Task> change) : DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Hits == 0 && command.Transaction is not null && command.CommandText.Contains("\"Users\"", StringComparison.Ordinal)) { Hits++; await change(); }
            return result;
        }
    }
}
