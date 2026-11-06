using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentCreateSellerSelectionTests
{
    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var invalid in new[] { "external", "company-archived", "company-draft", "company-missing", "branch-without-company", "branch-other", "branch-archived", "branch-missing" })
                yield return [api, invalid];
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidSellerCannotBeStoredOrChangeMoney(int api, string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false); var seller = await SeedSellerAsync(db);
        var model = PaymentFor(fixture); await SetInvalidAsync(db, model, seller, invalid);
        await AssertInvalidSelectionAsync(db, fixture, seller, api, model, invalid);
    }

    internal static async Task AssertInvalidSelectionAsync(PaymentContext db, Fixture fixture, Seller seller, int api, Payment model, string invalid)
    {
        var expectedError = await new PayPlanner.Api.Services.Companies.DocumentSellerService(db).ValidateSelectionAsync(model.SellerCompanyId, model.SellerBranchId, default);
        Assert.NotNull(expectedError); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        if (invalid == "branch-without-company")
        {
            // This tuple cannot be a registered key: the public registration
            // rejects its shape before the database ownership constraint.
            // Do not bypass that constraint in fixture setup.
            Assert.Equal("Для выбора подразделения укажите компанию.", expectedError);
            var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
            using var services = new ServiceCollection().BuildServiceProvider();
            controller.HttpContext.RequestServices = services;
            var body = JsonSerializer.SerializeToElement(new
            {
                operation = "primary",
                sellerCompanyId = model.SellerCompanyId,
                sellerBranchId = model.SellerBranchId,
                primaryPaymentId = (int?)null,
            });
            var rejected = Assert.IsType<BadRequestObjectResult>(await controller.RegisterCreateAttempt(body, default));
            Assert.Equal("Проверьте назначение попытки и выбранную компанию.",
                JsonSerializer.SerializeToElement(rejected.Value).GetProperty("message").GetString());
            Assert.Empty(await db.PaymentCreateAttempts.AsNoTracking().ToArrayAsync());
            Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));

            // A legitimate key cannot be reused with the malformed selection.
            var valid = PaymentFor(fixture); valid.SellerCompanyId = seller.Company; valid.SellerBranchId = seller.Branch;
            var key = await LegacyPaymentOperationKeyTests.RegisterAsync(db, fixture, valid);
            var beforeAttempt = JsonSerializer.Serialize(await db.PaymentCreateAttempts.AsNoTracking().SingleAsync());
            var conflict = Assert.IsType<ConflictObjectResult>(await LegacyPaymentOperationKeyTests.CreateAsync(db, fixture, api, key.ToString("D"), model));
            Assert.Equal("payment_operation_conflict", JsonSerializer.SerializeToElement(conflict.Value).GetProperty("code").GetString());
            Assert.Equal(beforeAttempt, JsonSerializer.Serialize(await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()));
        }
        else
        {
            var response = Assert.IsType<BadRequestObjectResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model));
            Assert.Equal(expectedError, JsonSerializer.SerializeToElement(response.Value).GetProperty("message").GetString());
        }
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    public async Task ActiveOwnSellerNeedsPaymentCreationNotProfileEditing(int api, bool legal, bool branch)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legal); var seller = await SeedSellerAsync(db);
        await AssertValidAsync(db, fixture, seller, api, branch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NoExplicitSellerDoesNotRequireAnOwnCompany(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        Assert.Empty(await db.SystemSettings.Where(s => s.Key.StartsWith("legalEntities.mainCompany")).ToArrayAsync());
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, PaymentFor(fixture))).Value);
        Assert.Null(saved.SellerCompanyId); Assert.Null(saved.SellerBranchId); Assert.Null(saved.SellerSnapshotJson); Assert.Null(saved.SellerSnapshotPolicy);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CurrentActorAndPaymentRightsAreCheckedBeforeSellerDetails(int api, bool inactive)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var seller = await SeedSellerAsync(db); var model = PaymentFor(fixture); await SetInvalidAsync(db, model, seller, "branch-other");
        if (inactive) (await db.Users.SingleAsync()).IsActive = false;
        else (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanCreate = false;
        await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        var result = await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model);
        if (inactive) Assert.IsType<UnauthorizedResult>(result); else Assert.IsType<ForbidResult>(result);
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WireCannotChooseACounterpartyAsOurSeller(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var seller = await SeedSellerAsync(db); var model = PaymentFor(fixture); await SetInvalidAsync(db, model, seller, "external");
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        using var server = PaymentCreateGraphTests.Server(db, fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        using var response = await client.PostAsJsonAsync(api == 0 ? "/api/payments" : $"/api/v{api}/payments", model);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ValidSelectionAndPaymentRollBackTogetherOnSaveFailure(int api)
    {
        var fault = new FailSecondSave(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault);
        var fixture = await SeedAsync(db, false); var seller = await SeedSellerAsync(db);
        await AssertRollbackAsync(db, fixture, seller, api, fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DedicatedAttemptKeepsItsSelectionAndRechecksIt(bool retire)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var seller = await SeedSellerAsync(db); await AssertAttemptAsync(db, fixture, seller, retire);
    }

    internal sealed record Seller(int Company, int Branch, int OtherCompany, int OtherBranch);
    internal static async Task<Seller> SeedSellerAsync(PaymentContext db)
    {
        var own = new LegalEntity { ShortName = "Our active seller", ProfileStatus = CompanyProfileStatuses.Active };
        var external = new LegalEntity { ShortName = "External counterparty", ProfileStatus = CompanyProfileStatuses.Active };
        db.LegalEntities.AddRange(own, external); await db.SaveChangesAsync();
        var branch = new CompanyBranch { LegalEntityId = own.Id, Name = "Own office" };
        var other = new CompanyBranch { LegalEntityId = external.Id, Name = "External office" };
        db.CompanyBranches.AddRange(branch, other);
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{own.Id}]" });
        await db.SaveChangesAsync(); return new(own.Id, branch.Id, external.Id, other.Id);
    }

    internal static async Task SetInvalidAsync(PaymentContext db, Payment model, Seller seller, string invalid)
    {
        model.SellerCompanyId = seller.Company; model.SellerBranchId = seller.Branch;
        switch (invalid)
        {
            case "external": model.SellerCompanyId = seller.OtherCompany; model.SellerBranchId = seller.OtherBranch; break;
            case "company-archived": (await db.LegalEntities.SingleAsync(c => c.Id == seller.Company)).ProfileStatus = CompanyProfileStatuses.Archived; break;
            case "company-draft": (await db.LegalEntities.SingleAsync(c => c.Id == seller.Company)).ProfileStatus = CompanyProfileStatuses.Draft; break;
            case "company-missing": model.SellerCompanyId = int.MaxValue; model.SellerBranchId = null; break;
            case "branch-without-company": model.SellerCompanyId = null; break;
            case "branch-other": model.SellerBranchId = seller.OtherBranch; break;
            case "branch-archived": (await db.CompanyBranches.SingleAsync(b => b.Id == seller.Branch)).Status = CompanyProfileStatuses.Archived; break;
            case "branch-missing": model.SellerBranchId = int.MaxValue; break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }
        await db.SaveChangesAsync();
    }

    internal static async Task AssertValidAsync(PaymentContext db, Fixture fixture, Seller seller, int api, bool branch)
    {
        Assert.Empty(await db.UserCompanyAccesses.ToArrayAsync());
        Assert.DoesNotContain(await db.RolePermissions.ToArrayAsync(), p => p.Section == "companyProfile");
        var model = PaymentFor(fixture); model.SellerCompanyId = seller.Company; model.SellerBranchId = branch ? seller.Branch : null;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(seller.Company, stored.SellerCompanyId); Assert.Equal(model.SellerBranchId, stored.SellerBranchId);
        Assert.Equal(model.ClientId, stored.ClientId); Assert.Equal(model.LegalEntityId, stored.LegalEntityId);
        Assert.Null(stored.SellerSnapshotJson); Assert.Null(stored.SellerSnapshotPolicy); Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
    }

    internal static async Task AssertRollbackAsync(PaymentContext db, Fixture fixture, Seller seller, int api, FailSecondSave fault)
    {
        var before = await PaymentCreateGraphTests.SnapshotAsync(db); var model = PaymentFor(fixture);
        model.SellerCompanyId = seller.Company; model.SellerBranchId = seller.Branch; fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => PaymentCreateGraphTests.CreateAsync(db, fixture, api, model));
        fault.Enabled = false; Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
        Assert.Equal(CompanyProfileStatuses.Active, (await db.LegalEntities.SingleAsync(c => c.Id == seller.Company)).ProfileStatus);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    internal static async Task AssertAttemptAsync(PaymentContext db, Fixture fixture, Seller seller, bool retire)
    {
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, fixture);
        var registration = JsonSerializer.SerializeToElement(new { operation = "primary", sellerCompanyId = seller.Company, sellerBranchId = seller.Branch });
        var attempt = Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(await controller.RegisterCreateAttempt(registration, default)).Value);
        var model = PaymentFor(fixture);
        var node = JsonNode.Parse(PaymentCreateFinancialBoundaryTests.Command(model).GetRawText())!.AsObject();
        node["sellerCompanyId"] = seller.Company; node["sellerBranchId"] = seller.Branch;
        if (retire)
        {
            (await db.CompanyBranches.SingleAsync(b => b.Id == seller.Branch)).Status = CompanyProfileStatuses.Archived;
            await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
            Assert.IsType<ForbidResult>(await controller.CommitCreateAttempt(attempt.AttemptId, JsonSerializer.SerializeToElement(node), default));
            Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
            Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.SingleAsync()).State);
        }
        else
        {
            var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, JsonSerializer.SerializeToElement(node), default)).Value);
            Assert.Equal(seller.Company, saved.SellerCompanyId); Assert.Equal(seller.Branch, saved.SellerBranchId);
            Assert.Equal(fixture.OwnerId, saved.ClientId); Assert.Null(saved.SellerSnapshotJson);
            Assert.Equal(PaymentCreateAttempt.Committed, (await db.PaymentCreateAttempts.SingleAsync()).State);
        }
    }

    internal sealed class FailSecondSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        private int saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && ++saves == 2) throw new IOException("Synthetic seller-selected payment save failure");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class PaymentCreateSellerSelectionPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var invalid in new[] { "external", "company-archived", "branch-other", "branch-without-company" }) yield return [api, invalid];
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task NativeInvalidSelectionLeavesTheDatabaseUnchanged(int api, string invalid)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(db);
        var model = PaymentFor(fixture); await PaymentCreateSellerSelectionTests.SetInvalidAsync(db, model, seller, invalid);
        await PaymentCreateSellerSelectionTests.AssertInvalidSelectionAsync(db, fixture, seller, api, model, invalid);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task NativeValidSellerDoesNotReplaceTheCounterparty(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legal); var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(db);
        await PaymentCreateSellerSelectionTests.AssertValidAsync(db, fixture, seller, api, true);
    }

    public static IEnumerable<object[]> Races()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var selection in new[] { "registry", "company", "branch" }) yield return [api, selection];
    }

    [Theory]
    [MemberData(nameof(Races))]
    public async Task ChangedSelectionBeforeItsLockCannotBeUsed(int api, string selection)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforeSelectionLock(selection);
        await using var creating = Open(database.ConnectionString, pause); await using var changing = Open(database.ConnectionString);
        var fixture = await SeedAsync(creating, false); var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(creating);
        var model = PaymentFor(fixture); model.SellerCompanyId = seller.Company; model.SellerBranchId = seller.Branch;
        var before = await PaymentCreateGraphTests.SnapshotAsync(creating); pause.Enabled = true;
        var request = PaymentCreateGraphTests.CreateAsync(creating, fixture, api, model);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await ChangeSelectionAsync(changing, seller, selection);
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(await request.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(creating));
        Assert.Equal(1, await creating.Payments.CountAsync()); Assert.Null(creating.Database.CurrentTransaction);
        await AssertSelectionChangedAsync(changing, seller, selection);
    }

    [Theory]
    [MemberData(nameof(Races))]
    public async Task SelectedSellerStaysStableUntilPaymentCommit(int api, string selection)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforeSave();
        await using var creating = Open(database.ConnectionString, pause); await using var changing = Open(database.ConnectionString);
        var fixture = await SeedAsync(creating, false); var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(creating);
        var model = PaymentFor(fixture); model.SellerCompanyId = seller.Company; model.SellerBranchId = seller.Branch; pause.Enabled = true;
        var request = PaymentCreateGraphTests.CreateAsync(creating, fixture, api, model);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var transaction = await changing.Database.BeginTransactionAsync();
            await changing.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'");
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ChangeSelectionAsync(changing, seller, selection));
            Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, error.SqlState); await transaction.RollbackAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>(await request.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(2, await creating.Payments.CountAsync()); Assert.Null(creating.Database.CurrentTransaction);
        // Once the creator commits, a later legitimate archive/registry edit can proceed.
        await ChangeSelectionAsync(changing, seller, selection); await AssertSelectionChangedAsync(changing, seller, selection);
        Assert.Equal(2, await changing.Payments.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeSaveFailureRollsBackTheSelectedPayment(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); var fault = new PaymentCreateSellerSelectionTests.FailSecondSave();
        await using var db = Open(database.ConnectionString, fault); var fixture = await SeedAsync(db, false);
        var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(db);
        await PaymentCreateSellerSelectionTests.AssertRollbackAsync(db, fixture, seller, api, fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDedicatedProtocolKeepsItsSellerRules(bool retire)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(db);
        await PaymentCreateSellerSelectionTests.AssertAttemptAsync(db, fixture, seller, retire);
    }

    private static Task ChangeSelectionAsync(PaymentContext db, PaymentCreateSellerSelectionTests.Seller seller, string selection)
        => selection switch
        {
            "registry" => db.Database.ExecuteSqlRawAsync("UPDATE \"SystemSettings\" SET \"Value\" = '[]' WHERE \"Key\" = 'legalEntities.mainCompanyIds'"),
            "company" => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"LegalEntities\" SET \"ProfileStatus\" = {CompanyProfileStatuses.Archived} WHERE \"Id\" = {seller.Company}"),
            "branch" => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"CompanyBranches\" SET \"Status\" = {CompanyProfileStatuses.Archived} WHERE \"Id\" = {seller.Branch}"),
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };

    private static async Task AssertSelectionChangedAsync(PaymentContext db, PaymentCreateSellerSelectionTests.Seller seller, string selection)
    {
        if (selection == "registry") Assert.Equal("[]", (await db.SystemSettings.AsNoTracking().SingleAsync(s => s.Key == "legalEntities.mainCompanyIds")).Value);
        else if (selection == "company") Assert.Equal(CompanyProfileStatuses.Archived, (await db.LegalEntities.AsNoTracking().SingleAsync(c => c.Id == seller.Company)).ProfileStatus);
        else Assert.Equal(CompanyProfileStatuses.Archived, (await db.CompanyBranches.AsNoTracking().SingleAsync(b => b.Id == seller.Branch)).Status);
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class BeforeSelectionLock(string selection) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            var table = selection == "registry" ? "SystemSettings" : selection == "company" ? "LegalEntities" : "CompanyBranches";
            if (Enabled && command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal) && command.CommandText.Contains("NOWAIT", StringComparison.Ordinal))
            {
                Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }

    private sealed class BeforeSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled) { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}
