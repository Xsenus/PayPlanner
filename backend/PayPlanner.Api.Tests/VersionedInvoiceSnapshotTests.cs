using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class VersionedInvoiceSnapshotTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> Policies()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var api in new[] { 1, 2 })
                foreach (var timing in new[] { DocumentSellerSnapshotPolicy.FirstFile, DocumentSellerSnapshotPolicy.OnCreate })
                    foreach (var useDefault in new[] { false, true }) yield return [postgres, api, timing, useDefault];
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public Task CreationCapturesConfiguredPolicy(bool postgres, int api, string timing, bool useDefault)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedAsync(db, false);
            var seller = await ConfigureAsync(db, timing);
            var old = await db.Payments.SingleAsync(); old.SellerSnapshotJson = "{\"ShortName\":\"Issued seller\",\"ImagesFrozen\":true}";
            old.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile; await db.SaveChangesAsync();
            var original = old.SellerSnapshotJson;
            var model = InvoiceFor(fixture);
            if (!useDefault) { model.SellerCompanyId = seller.Company; model.SellerBranchId = seller.Branch; }
            // These output fields are not caller input, even when a serialized entity is posted.
            model.SellerSnapshotPolicy = timing == DocumentSellerSnapshotPolicy.OnCreate ? DocumentSellerSnapshotPolicy.FirstFile : DocumentSellerSnapshotPolicy.OnCreate;
            model.SellerSnapshotJson = "{\"ShortName\":\"Injected seller\"}";
            Assert.DoesNotContain(await db.RolePermissions.ToArrayAsync(), p => p.Section == "companyProfile");
            var result = Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model));
            var saved = Assert.IsType<Payment>(result.Value);
            var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
            Assert.Equal(DocumentKind.Invoice, stored.DocumentKind); Assert.Equal(timing, stored.SellerSnapshotPolicy);
            Assert.Equal(timing, saved.SellerSnapshotPolicy);
            if (timing == DocumentSellerSnapshotPolicy.OnCreate)
            {
                Assert.Equal(seller.Company, stored.SellerCompanyId); Assert.Equal(useDefault ? null : seller.Branch, stored.SellerBranchId);
                var metadata = Assert.IsType<SellerSnapshotMetadata>(SellerLegalEntityComposer.ReadMetadata(stored.SellerSnapshotJson));
                Assert.Equal(timing, metadata.FreezeReason); Assert.NotNull(metadata.FrozenAtUtc);
                Assert.True(metadata.ImagePrintDefaultsFrozen); Assert.True(metadata.ImagePrintDefaults!.Invoice!.IncludeLogo);
                Assert.False(metadata.ImagePrintDefaults.Invoice.IncludeSeal); Assert.True(metadata.ImagePrintDefaults.Invoice.IncludeSignature);
                Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
                Assert.Contains("Our active seller", stored.SellerSnapshotJson); Assert.DoesNotContain("Injected seller", stored.SellerSnapshotJson);
            }
            else { Assert.Null(stored.SellerSnapshotJson); Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync()); }
            var setting = await db.SystemSettings.SingleAsync(x => x.Key == DocumentSellerSnapshotPolicy.SettingKey);
            setting.Value = timing == DocumentSellerSnapshotPolicy.OnCreate ? DocumentSellerSnapshotPolicy.FirstFile : DocumentSellerSnapshotPolicy.OnCreate;
            (await db.LegalEntities.SingleAsync(x => x.Id == seller.Company)).ShortName = "Changed later";
            await db.SaveChangesAsync();
            Assert.Equal(original, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).SellerSnapshotJson);
            Assert.Equal(stored.SellerSnapshotJson, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id)).SellerSnapshotJson);
            Assert.Equal(timing, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id)).SellerSnapshotPolicy);
            var resolved = await new DocumentSellerService(db).ResolveAsync(stored, null, true, default);
            Assert.Equal(timing == DocumentSellerSnapshotPolicy.OnCreate ? "Our active seller" : "Changed later", resolved!.ShortName);
            Assert.Equal(timing, SellerLegalEntityComposer.ReadMetadata(stored.SellerSnapshotJson)!.FreezeReason);
            Assert.Equal(original, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).SellerSnapshotJson);
            Assert.Null(db.Database.CurrentTransaction);
        });

    public static IEnumerable<object[]> NonSellerDocuments()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var api in new[] { 0, 1, 2 })
                foreach (var kind in new[] { "payment", "accrual", "expense-invoice" }) yield return [postgres, api, kind];
    }

    [Theory]
    [MemberData(nameof(NonSellerDocuments))]
    public Task OtherFinancialRecordsDoNotRequireOrCaptureOurCompany(bool postgres, int api, string kind)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedAsync(db, false);
            db.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
            await db.SaveChangesAsync();
            var model = PaymentFor(fixture);
            model.DocumentKind = kind == "accrual" ? DocumentKind.Accrual : kind == "expense-invoice" ? DocumentKind.Invoice : DocumentKind.Payment;
            if (kind == "expense-invoice") model.Type = PaymentType.Expense;
            var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
            Assert.Null(saved.SellerSnapshotJson); Assert.Null(saved.SellerSnapshotPolicy); Assert.Null(saved.SellerCompanyId);
            Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
            if (api == 0 && kind == "expense-invoice") Assert.Equal(DocumentKind.Payment, saved.DocumentKind);
        });

    public static IEnumerable<object[]> Errors()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var api in new[] { 1, 2 })
                foreach (var error in new[] { "no-default", "oversized", "defaults", "denied", "inactive" }) yield return [postgres, api, error];
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public Task RefusalLeavesNoInvoiceImageVersionsOrFinancialChanges(bool postgres, int api, string error)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedAsync(db, false); var seller = await ConfigureAsync(db, DocumentSellerSnapshotPolicy.OnCreate);
            switch (error)
            {
                case "no-default": db.SystemSettings.RemoveRange(await db.SystemSettings.Where(x => x.Key.StartsWith("legalEntities.mainCompany")).ToArrayAsync()); break;
                case "oversized":
                    var company = await db.LegalEntities.SingleAsync(x => x.Id == seller.Company);
                    company.BankAccountsJson = JsonSerializer.Serialize(new[] { new { id = "selected", accountNumber = "40702810900000000001", bik = "044525225", bankName = "Synthetic", isPrimary = true, importedDetails = new string('B', 11000) } });
                    company.DocumentDirectorJson = JsonSerializer.Serialize(new { fullName = "Synthetic", position = "Director", documentBasis = new string('P', 5000) });
                    Assert.True(SellerLegalEntityComposer.ToSnapshotJson(SellerLegalEntityComposer.Compose(company, null)).Length > DocumentSellerService.MaxSnapshotLength);
                    break;
                case "defaults": (await db.SystemSettings.SingleAsync(x => x.Key == CompanyDocumentImagePrintDefaultsService.Key(seller.Company))).Value = "{broken"; break;
                case "denied": (await db.RolePermissions.SingleAsync(x => x.Section == "payments")).CanCreate = false; break;
                case "inactive": (await db.Users.SingleAsync(x => x.Id == fixture.ActorId)).IsActive = false; break;
            }
            await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
            var result = await PaymentCreateGraphTests.CreateAsync(db, fixture, api, InvoiceFor(fixture));
            if (error == "denied") Assert.IsType<ForbidResult>(result);
            else if (error == "inactive") Assert.IsType<UnauthorizedResult>(result);
            else Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
            Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
        });

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public Task SaveFailureRollsBackSnapshotVersionsAndMoney(bool postgres, int api)
    {
        var fault = new FailSnapshotSave();
        return RunAsync(postgres, async db =>
        {
            var fixture = await SeedAsync(db, false); await ConfigureAsync(db, DocumentSellerSnapshotPolicy.OnCreate);
            var before = await PaymentCreateGraphTests.SnapshotAsync(db); fault.Enabled = true;
            await Assert.ThrowsAsync<IOException>(() => PaymentCreateGraphTests.CreateAsync(db, fixture, api, InvoiceFor(fixture)));
            Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
            Assert.Null(db.Database.CurrentTransaction);
        }, fault);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HttpReturnsCanonicalSnapshotAndFriendlyRefusal(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var seller = await ConfigureAsync(db, DocumentSellerSnapshotPolicy.OnCreate);
        using var server = PaymentCreateGraphTests.Server(db, fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v{api}/payments", InvoiceFor(fixture));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<Payment>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;
        Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, saved.SellerSnapshotPolicy); Assert.NotNull(saved.SellerSnapshotJson);
        Assert.Equal(seller.Company, saved.SellerCompanyId);
        (await db.SystemSettings.SingleAsync(x => x.Key == CompanyDocumentImagePrintDefaultsService.Key(seller.Company))).Value = "{broken";
        await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        using var refused = await client.PostAsJsonAsync($"/api/v{api}/payments", InvoiceFor(fixture));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); Assert.Contains("message", await refused.Content.ReadAsStringAsync());
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task NativeDefaultSellerIsRecheckedAndHeldThroughFinancialCommit(int api, bool beforeLock)
    {
        await using var database = await template.CreateDatabaseAsync();
        var gate = new SnapshotGate(beforeLock);
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate);
        await using var db = new PostgreSqlPaymentContext(options.Options);
        await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var fixture = await SeedAsync(db, false); var seller = await ConfigureAsync(db, DocumentSellerSnapshotPolicy.OnCreate);
        var before = await PaymentCreateGraphTests.SnapshotAsync(db); gate.Enabled = true;
        var write = PaymentCreateGraphTests.CreateAsync(db, fixture, api, InvoiceFor(fixture));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (beforeLock)
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"LegalEntities\" SET \"ProfileStatus\" = {CompanyProfileStatuses.Archived} WHERE \"Id\" = {seller.Company}");
            else
            {
                await using var transaction = await other.Database.BeginTransactionAsync();
                await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'");
                var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => other.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"LegalEntities\" SET \"ShortName\" = {"Competing edit"} WHERE \"Id\" = {seller.Company}"));
                Assert.Equal("55P03", error.SqlState); await transaction.RollbackAsync();
            }
            gate.Release.TrySetResult();
            var result = await write.WaitAsync(TimeSpan.FromSeconds(30));
            if (beforeLock)
            {
                Assert.IsType<ConflictObjectResult>(result); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
                Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
            }
            else
            {
                var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(result).Value);
                Assert.Contains("Our active seller", saved.SellerSnapshotJson); Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"LegalEntities\" SET \"ShortName\" = {"Edit after commit"} WHERE \"Id\" = {seller.Company}");
                Assert.Equal(saved.SellerSnapshotJson, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id)).SellerSnapshotJson);
            }
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally { gate.Release.TrySetResult(); await write.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    internal static Payment InvoiceFor(Fixture fixture)
    {
        var model = PaymentFor(fixture); model.DocumentKind = DocumentKind.Invoice; return model;
    }

    internal static async Task<PaymentCreateSellerSelectionTests.Seller> ConfigureAsync(PaymentContext db, string timing)
    {
        var seller = await PaymentCreateSellerSelectionTests.SeedSellerAsync(db);
        var company = await db.LegalEntities.SingleAsync(x => x.Id == seller.Company);
        company.LogoDataUrl = "data:image/png;base64,c3ludGhldGljLWxvZ28=";
        company.SealDataUrl = "data:image/png;base64,c3ludGhldGljLXNlYWw=";
        company.DirectorSignatureDataUrl = "data:image/png;base64,c3ludGhldGljLXNpZ24=";
        db.SystemSettings.AddRange(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = timing },
            new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(seller.Company), Value = "{\"version\":1,\"invoice\":{\"includeLogo\":true,\"includeSeal\":false,\"includeSignature\":true},\"act\":null,\"contract\":null}" });
        await db.SaveChangesAsync(); return seller;
    }

    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test, IInterceptor? interceptor = null)
    {
        if (postgres)
        {
            await using var database = await template.CreateDatabaseAsync();
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            await using var db = new PostgreSqlPaymentContext(options.Options); await test(db);
        }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor); await test(db); }
    }

    private sealed class FailSnapshotSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<Payment>().Any(x => x.Entity.Id != 0 && x.Entity.SellerSnapshotJson is not null))
                throw new IOException("Synthetic invoice snapshot save failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SnapshotGate(bool beforeLock) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            var text = command.CommandText;
            var match = beforeLock ? text.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal) && text.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal)
                : text.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal) && text.Contains("LogoDataUrl", StringComparison.Ordinal);
            if (Enabled && match) { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}
