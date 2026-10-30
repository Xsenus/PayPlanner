using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Fact]
    public async Task CompanyCreate_TwoSqliteConnectionsDoNotInsertDuplicateInn()
    {
        await using var seed = await CreateContextAsync();
        var userId = await CreateUserAsync(seed, "admin");
        var connection = $"Data Source=company_create_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = new SqliteConnection(connection); await anchor.OpenAsync();
        ((SqliteConnection)seed.Database.GetDbConnection()).BackupDatabase(anchor);
        var barrier = new CompanyCreateSaveBarrier();
        await using var first = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(barrier).Options);
        await using var second = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await AssertCompanyCreateRace(first, second, userId, barrier);
    }

    [Fact]
    public async Task CompanyCreate_PostgreSqlConcurrentInsertRejectsOneWriter()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var userId = await CreateUserAsync(seed, "admin");
        var barrier = new CompanyCreateSaveBarrier();
        await using var first = CompanyCreatePgContext(database.ConnectionString, barrier);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        await AssertCompanyCreateRace(first, second, userId, barrier);
    }

    private static PostgreSqlPaymentContext CompanyCreatePgContext(string connection, IInterceptor? interceptor = null)
    {
        // Match production's split-query profile, not only EF's single-query default.
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection,
            postgres => postgres.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PostgreSqlPaymentContext(options.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_PostgreSqlCreateAndInnEditCannotBothClaimOneInn(bool editFirst)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var userId = await CreateUserAsync(seed, "admin");
        var existingId = await CreateLegalEntityAsync(seed, "Existing card");
        var barrier = new CompanyCreateSaveBarrier { IncludeUpdates = true };
        await using var first = CompanyCreatePgContext(database.ConnectionString, barrier);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        var creation = new LegalEntityRequest { ShortName = "New card", Inn = "7700000105" };
        var edit = new LegalEntityRequest { ShortName = "Edited card", Inn = "7700000105" };
        var earlyController = CreateController(first, userId);
        var laterController = CreateController(second, userId);
        var pending = editFirst ? earlyController.Update(existingId, edit, default) : earlyController.Create(creation, default);
        ActionResult<LegalEntityDetailDto>? winner = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            winner = await (editFirst ? laterController.Create(creation, default) : laterController.Update(existingId, edit, default)).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { barrier.Release.TrySetResult(true); }
        Assert.IsType<ConflictObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        if (editFirst) Assert.IsType<CreatedResult>(winner!.Result); else Assert.IsType<OkObjectResult>(winner!.Result);
        Assert.Single(await second.LegalEntities.AsNoTracking().Where(row => row.Inn == "7700000105").ToArrayAsync());
        var original = await second.LegalEntities.AsNoTracking().SingleAsync(row => row.Id == existingId);
        Assert.Equal(editFirst ? "Existing card" : "Edited card", original.ShortName);
        Assert.Equal(editFirst ? null : "7700000105", original.Inn);
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
    }

    private static async Task AssertCompanyCreateRace(PaymentContext first, PaymentContext second, int userId, CompanyCreateSaveBarrier barrier)
    {
        const string inn = "7700000101";
        var pending = CreateController(first, userId).Create(new LegalEntityRequest { ShortName = "First writer", Inn = inn }, default);
        ActionResult<LegalEntityDetailDto>? later = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            later = await CreateController(second, userId).Create(new LegalEntityRequest { ShortName = "Second writer", Inn = " " + inn + " " }, default).WaitAsync(TimeSpan.FromSeconds(12));
        }
        finally { barrier.Release.TrySetResult(true); }
        var earlier = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        var responses = new[] { earlier.Result, later!.Result };
        Assert.Single(responses.OfType<CreatedResult>());
        Assert.Single(responses.OfType<ConflictObjectResult>());
        var saved = Assert.IsType<LegalEntityDetailDto>(responses.OfType<CreatedResult>().Single().Value);
        var stored = Assert.Single(await second.LegalEntities.AsNoTracking().ToArrayAsync());
        Assert.Equal(saved.Id, stored.Id); Assert.Equal(saved.ShortName, stored.ShortName); Assert.Equal(inn, stored.Inn);
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
        Assert.Equal(1, barrier.Pauses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_SecondSaveFailureRollsBackWholeCard(bool imageDefaults)
    {
        var failure = new CompanyCreateSecondSaveFailure();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var userId = await CreateUserAsync(db, "admin");
        var client = new Client { Name = "Existing client" }; db.Clients.Add(client); await db.SaveChangesAsync();
        var request = new LegalEntityRequest
        {
            ShortName = "Must rollback",
            Inn = "7700000102",
            ClientIds = [client.Id],
            Addresses = [new() { Value = "Must rollback address" }],
            DocumentImagePrintDefaultsChanged = imageDefaults,
            DocumentImagePrintDefaults = imageDefaults ? PrintDefaultsRequest(7) : null,
        };
        failure.Remaining = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController(db, userId).Create(request, default));

        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.AddressRecords.AsNoTracking().ToArrayAsync());
        Assert.Null((await db.Clients.AsNoTracking().SingleAsync()).LegalEntityId);
        Assert.Empty(await db.SystemSettings.Where(row => row.Key.StartsWith(CompanyDocumentImagePrintDefaultsService.KeyPrefix)).ToArrayAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("foreign-own", false)]
    [InlineData("foreign-regular", false)]
    [InlineData("no-view", false)]
    [InlineData("assigned-own", true)]
    [InlineData("visible-regular", true)]
    public async Task CompanyCreate_DuplicateInnDisclosesOnlyAnAuthorizedCard(string scope, bool canSeeDuplicate)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, userId, canCreate: true, canEdit: false, canDelete: false);
        var user = await db.Users.SingleAsync(row => row.Id == userId);
        var permission = await db.RolePermissions.SingleAsync(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.LegalEntities);
        permission.CanCreate = true; permission.CanView = scope != "no-view"; permission.ViewScope = scope == "no-view" ? DataVisibilityScope.All : DataVisibilityScope.Own;
        var duplicate = new LegalEntity { Id = 445, ShortName = "Protected duplicate name", Inn = "7700000103", ResponsibleUserId = scope == "visible-regular" ? userId : null };
        db.LegalEntities.Add(duplicate);
        if (scope.EndsWith("own", StringComparison.Ordinal))
        {
            db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = "[445]" });
            if (scope == "assigned-own")
            {
                db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true });
                db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = duplicate.Id });
            }
        }
        await db.SaveChangesAsync();
        // A stale admin token must not turn this into permission to read the duplicate.
        var response = await CreateController(db, userId, isAdmin: true)
            .Create(new LegalEntityRequest { ShortName = "New card", Inn = " 7700000103 " }, default);
        if (scope == "no-view")
        {
            Assert.IsType<UnauthorizedResult>(response.Result);
            Assert.Single(await db.LegalEntities.AsNoTracking().ToArrayAsync());
            return;
        }
        var result = Assert.IsType<ConflictObjectResult>(response.Result);
        var text = Assert.IsType<string>(result.Value);
        Assert.Equal(canSeeDuplicate, text.Contains(duplicate.ShortName, StringComparison.Ordinal));
        Assert.Equal(canSeeDuplicate, text.Contains("ID: 445", StringComparison.Ordinal));
        if (!canSeeDuplicate) Assert.Equal("Юридическое лицо с таким ИНН уже существует.", text);
        Assert.Single(await db.LegalEntities.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_RejectsDirtyOrExternalUnitOfWorkWithoutSavingIt(bool ambient)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "admin");
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var unrelated = new Client { Name = "Caller owns this" }; db.Clients.Add(unrelated);
        var result = await CreateController(db, userId).Create(new LegalEntityRequest { ShortName = "Do not insert" }, default);
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(EntityState.Added, db.Entry(unrelated).State); Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task CompanyCreate_BlankInnAllowsSeparateCardsAndHistoricalDuplicatesAreNotRewritten()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "admin");
        var legacy = new[] { new LegalEntity { ShortName = "Legacy first", Inn = "7700000104", Notes = "Keep first" }, new LegalEntity { ShortName = "Legacy second", Inn = " 7700000104 ", Notes = "Keep second" } };
        db.LegalEntities.AddRange(legacy); await db.SaveChangesAsync();
        var controller = CreateController(db, userId);
        Assert.IsType<ConflictObjectResult>((await controller.Create(new LegalEntityRequest { ShortName = "Rejected third", Inn = "7700000104" }, default)).Result);
        foreach (var inn in new string?[] { null, "", "  " })
            Assert.IsType<CreatedResult>((await controller.Create(new LegalEntityRequest { ShortName = "No INN", Inn = inn }, default)).Result);
        var rows = await db.LegalEntities.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(5, rows.Length);
        Assert.Equal(legacy[0].Inn, rows[0].Inn); Assert.Equal(legacy[0].Notes, rows[0].Notes);
        Assert.Equal(legacy[1].Inn, rows[1].Inn); Assert.Equal(legacy[1].Notes, rows[1].Notes);
    }

    [Theory]
    [InlineData("no-create", 403)]
    [InlineData("new-role", 403)]
    [InlineData("inactive", 401)]
    [InlineData("anonymous", 401)]
    public async Task CompanyCreate_RechecksPermissionsBeforeInspectingPayload(string reason, int expectedStatus)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, userId, canCreate: false, canEdit: false, canDelete: false);
        var user = await db.Users.SingleAsync(row => row.Id == userId);
        if (reason == "new-role")
        {
            var role = new Role { Name = "New restricted role", Permissions = [new() { Section = RolePermissionCatalog.LegalEntities, CanView = true, CanCreate = false }] };
            db.Roles.Add(role); user.Role = role;
        }
        if (reason == "inactive") user.IsActive = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdmin: true);
        if (reason == "anonymous") controller.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var response = await controller.Create(new LegalEntityRequest { ShortName = "", SealDataUrl = "not an image" }, default);
        if (expectedStatus == 403) Assert.IsType<ForbidResult>(response.Result);
        else Assert.IsType<UnauthorizedResult>(response.Result);
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CompanyCreate_HttpCommitsCardAndRejectsTrimmedDuplicate()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "admin");
        using var server = PrintDefaultsServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Profile-Print-User", userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var created = await client.PostAsJsonAsync("/api/legal-entities", new LegalEntityRequest
        {
            ShortName = "HTTP company",
            Inn = " 7700000106 ",
            Addresses = [new() { Value = "HTTP address" }],
            DocumentImagePrintDefaults = PrintDefaultsRequest(7),
            DocumentImagePrintDefaultsChanged = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<LegalEntityDetailDto>())!;
        Assert.Equal("7700000106", saved.Inn); Assert.Single(saved.Addresses);
        Assert.True(CompanyProfileEditVersion.IsValid(saved.ProfileEditVersion));
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(7), saved.DocumentImagePrintDefaults);
        using var duplicate = await client.PostAsJsonAsync("/api/legal-entities", new LegalEntityRequest { ShortName = "Duplicate HTTP", Inn = "7700000106" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(await db.LegalEntities.AsNoTracking().ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_DuplicateInnDuringEditAlsoHidesOtherCompany(bool ownDuplicate)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, userId, canCreate: false, canEdit: true, canDelete: false);
        var roleId = (await db.Users.SingleAsync(row => row.Id == userId)).RoleId;
        (await db.RolePermissions.SingleAsync(row => row.RoleId == roleId && row.Section == RolePermissionCatalog.LegalEntities)).ViewScope = DataVisibilityScope.Own;
        var editable = new LegalEntity { ShortName = "Editable", ResponsibleUserId = userId };
        var hidden = new LegalEntity { ShortName = "Hidden duplicate", Inn = "7700000107" };
        db.LegalEntities.AddRange(editable, hidden); await db.SaveChangesAsync();
        if (ownDuplicate)
        {
            db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{hidden.Id}]" });
            await db.SaveChangesAsync();
        }
        var conflict = Assert.IsType<ConflictObjectResult>((await CreateController(db, userId, isAdmin: true)
            .Update(editable.Id, new LegalEntityRequest { ShortName = "Must not save", Inn = "7700000107" }, default)).Result);
        Assert.Equal("Юридическое лицо с таким ИНН уже существует.", conflict.Value);
        var stored = await db.LegalEntities.AsNoTracking().SingleAsync(row => row.Id == editable.Id);
        Assert.Equal("Editable", stored.ShortName); Assert.Null(stored.Inn);
    }

    private sealed class CompanyCreateSaveBarrier : SaveChangesInterceptor
    {
        public bool IncludeUpdates { get; init; }
        public int Pauses { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Pauses == 0 && eventData.Context!.ChangeTracker.Entries<LegalEntity>().Any(entry => entry.State == EntityState.Added || IncludeUpdates && entry.State == EntityState.Modified))
            {
                Pauses++; Entered.TrySetResult(true);
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            }
            return result;
        }
    }

    private sealed class CompanyCreateSecondSaveFailure : SaveChangesInterceptor
    {
        public int Remaining { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
            => Remaining > 0 && --Remaining == 0 ? throw new InvalidOperationException("Synthetic second-save failure") : ValueTask.FromResult(result);
    }
}
