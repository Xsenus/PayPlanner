using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class DatabaseImportServiceTests
{
    private const string HistoryImage = "data:image/png;base64,QUFBQUFB"; // Historical content is not a new image upload.
    private const string HistoryInn = "7712345678";
    private static string HistoryHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string HistoryAccounts(string id, string currency = "RUB") => new JsonArray(new JsonObject
    { ["id"] = id, ["accountNumber"] = "40702810000000000001", ["bik"] = "044525225", ["currency"] = currency, ["isPrimary"] = true, ["isArchived"] = false }).ToJsonString();
    private static SqliteConnection HistoryFile(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());

    private static async Task SeedHistorySourceAsync(string path, string mode = "first-file")
    {
        await using var connection = HistoryFile(path); await connection.OpenAsync();
        await using var db = CreateContext(connection); await db.Database.EnsureCreatedAsync();
        var company = new LegalEntity
        {
            ShortName = "Source company",
            Inn = HistoryInn,
            BankAccountsJson = HistoryAccounts("source-bank"),
            LogoDataUrl = mode == "absence" ? null : HistoryImage,
            SealDataUrl = mode == "absence" ? null : HistoryImage,
            DirectorSignatureDataUrl = mode == "absence" ? null : HistoryImage
        };
        db.LegalEntities.Add(company); await db.SaveChangesAsync();
        var branch = new CompanyBranch { LegalEntityId = company.Id, Name = "Historical office", Code = "BR-HISTORY", BankAccountId = "source-bank", Kpp = "770101001" };
        db.CompanyBranches.Add(branch);
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" }, new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        await db.SaveChangesAsync();
        var client = new Client { Name = "History client" };
        var contract = new Contract { Number = "HIST-C", Date = new DateTime(2026, 10, 1), StartDate = new DateTime(2026, 10, 1), IsOpenEnded = true, Amount = 100m };
        var payment = CreateInvoicePayment(new Invoice { Number = "HIST-I", Date = contract.Date }, client, null, "Historical invoice");
        payment.Amount = 100m;
        payment.Contract = contract;
        var act = new Act { Number = "HIST-A", Date = contract.Date, Amount = 100m, Client = client, Invoice = payment };
        foreach (var document in new IDocumentSeller[] { payment, act, contract })
        { document.SellerCompanyId = company.Id; document.SellerBranchId = branch.Id; document.SellerSnapshotPolicy = mode == "on-create" ? DocumentSellerSnapshotPolicy.OnCreate : DocumentSellerSnapshotPolicy.FirstFile; }
        db.AddRange(payment, act, contract, new Role { Name = "Imported only after preflight" }); await db.SaveChangesAsync();
        var flags = new DocumentImagePrintOptions(true, false, true);
        await new CompanyDocumentImagePrintDefaultsService(db).StageAsync(company.Id, new(flags, flags, flags), true); await db.SaveChangesAsync();
        foreach (var document in new IDocumentSeller[] { payment, act, contract })
        {
            if (mode == "pending") continue;
            if (mode == "legacy") document.SellerSnapshotJson = SellerLegalEntityComposer.ToSnapshotJson(SellerLegalEntityComposer.Compose(company, branch));
            else await new DocumentSellerService(db).ResolveAsync(document, company, true, default);
            var json = JsonNode.Parse(document.SellerSnapshotJson!)!.AsObject(); json["UnknownHistory"] = new JsonObject { ["text"] = "Do not replace historical text", ["number"] = 42 };
            document.SellerSnapshotJson = json.ToJsonString();
            await db.SaveChangesAsync();
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedHistoryTargetAsync(PaymentContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var foreign = new LegalEntity { ShortName = "Target sentinel", Inn = "9900000000" };
        var company = new LegalEntity { ShortName = "Target current profile", Inn = HistoryInn, BankAccountsJson = HistoryAccounts("target-bank"), LogoDataUrl = "target current logo" };
        db.AddRange(foreign, company); await db.SaveChangesAsync();
        db.CompanyBranches.Add(new CompanyBranch { LegalEntityId = foreign.Id, Name = "Unrelated branch" });
        db.CompanyBrandImageVersions.Add(new CompanyBrandImageVersion { LegalEntityId = foreign.Id, Kind = CompanyBrandImageKinds.Logo, DataUrl = "sentinel", Sha256 = HistoryHash("sentinel") });
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" }, new SystemSetting { Key = "notifications.email.enabled", Value = "false" },
            new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
        var client = new Client { Name = "Keep issued owner" };
        var contract = new Contract { Number = "KEEP-C", Date = new DateTime(2020, 1, 1), Amount = 321m, IsOpenEnded = true };
        var payment = CreateInvoicePayment(new Invoice { Number = "KEEP-I", Date = contract.Date }, client, null, "Keep issued invoice"); payment.Amount = 321m; payment.Contract = contract;
        var act = new Act { Number = "KEEP-A", Date = contract.Date, Amount = 321m, Client = client, Invoice = payment };
        foreach (var document in new IDocumentSeller[] { payment, act, contract })
        { document.SellerCompanyId = company.Id; document.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile; document.SellerSnapshotJson = SellerLegalEntityComposer.ToSnapshotJson(company, imagesFrozen: true); }
        db.AddRange(payment, act, contract); await db.SaveChangesAsync();
        db.Entry(act).Property("IsDeleted").CurrentValue = true; await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("first-file")]
    [InlineData("on-create")]
    [InlineData("absence")]
    [InlineData("legacy")]
    [InlineData("pending")]
    public async Task HistoryRemap_ImportsNewDocumentsAndPreservesIssuedTargetAndProfile(string mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path, mode);
            var sourceBytes = await File.ReadAllBytesAsync(path);
            await using var targetConnection = await CreateOpenConnectionAsync();
            await using (var db = CreateContext(targetConnection)) await SeedHistoryTargetAsync(db);
            Assert.True((await CreateService(targetConnection).ImportSqliteAsync(path)).Success);
            await using var target = CreateContext(targetConnection);
            await AssertHistoryResultAsync(path, target, mode);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path));
        }
        finally { TryDeleteFile(path); }
    }

    private static async Task AssertHistoryResultAsync(string path, PaymentContext target, string mode)
    {
        await using var connection = HistoryFile(path); await connection.OpenAsync(); await using var source = CreateContext(connection);
        var company = await target.LegalEntities.SingleAsync(c => c.Inn == HistoryInn);
        Assert.Equal("Target current profile", company.ShortName); Assert.Equal(HistoryAccounts("target-bank"), company.BankAccountsJson); Assert.Equal("target current logo", company.LogoDataUrl);
        var branch = await target.CompanyBranches.SingleAsync(b => b.Code == "BR-HISTORY");
        Assert.Equal(company.Id, branch.LegalEntityId); Assert.Equal("target-bank", branch.BankAccountId); Assert.NotEqual(1, branch.Id);
        var payment = await target.Payments.SingleAsync(p => p.Description == "Historical invoice");
        var act = await target.Acts.SingleAsync(a => a.Number == "HIST-A"); var contract = await target.Contracts.SingleAsync(c => c.Number == "HIST-C");
        var originalPayment = await source.Payments.SingleAsync(); var originalAct = await source.Acts.SingleAsync(); var originalContract = await source.Contracts.SingleAsync();
        foreach (var (original, imported) in new (IDocumentSeller, IDocumentSeller)[] { (originalPayment, payment), (originalAct, act), (originalContract, contract) })
        {
            Assert.Equal(company.Id, imported.SellerCompanyId); Assert.Equal(branch.Id, imported.SellerBranchId); Assert.Equal(original.SellerSnapshotPolicy, imported.SellerSnapshotPolicy);
            if (mode == "pending") { Assert.Null(imported.SellerSnapshotJson); continue; }
            var expected = JsonNode.Parse(original.SellerSnapshotJson!)!.AsObject(); var actual = JsonNode.Parse(imported.SellerSnapshotJson!)!.AsObject();
            expected["Id"] = actual["Id"]!.DeepClone();
            foreach (var field in new[] { "LogoVersionId", "SealVersionId", "SignatureVersionId" })
            {
                if (expected[field] is not JsonValue value || !value.TryGetValue<int>(out var sourceId)) continue;
                var sourceVersion = await source.CompanyBrandImageVersions.SingleAsync(i => i.Id == sourceId);
                var targetVersion = await target.CompanyBrandImageVersions.SingleAsync(i => i.Id == actual[field]!.GetValue<int>());
                Assert.Equal(company.Id, targetVersion.LegalEntityId); Assert.Equal(sourceVersion.Kind, targetVersion.Kind); Assert.Equal(sourceVersion.DataUrl, targetVersion.DataUrl); Assert.Equal(sourceVersion.Sha256, targetVersion.Sha256);
                expected[field] = actual[field]!.DeepClone();
            }
            Assert.True(JsonNode.DeepEquals(expected, actual));
            var seller = await new DocumentSellerService(target).ResolveAsync(imported, company, false, default);
            Assert.NotNull(seller); Assert.Equal("Source company", seller.ShortName);
            Assert.Equal(mode == "absence" ? null : mode == "legacy" ? company.LogoDataUrl : HistoryImage, seller.LogoDataUrl);
        }
        Assert.Equal(payment.Id, act.InvoiceId); Assert.Equal(contract.Id, payment.ContractId); Assert.Equal(100m, payment.Amount); Assert.Equal(100m, act.Amount); Assert.Equal(100m, contract.Amount);
        foreach (var frozen in new IDocumentSeller[] { await target.Payments.SingleAsync(p => p.Description == "Keep issued invoice"), await target.Acts.IgnoreQueryFilters().SingleAsync(a => a.Number == "KEEP-A"), await target.Contracts.SingleAsync(c => c.Number == "KEEP-C") })
            Assert.Equal(SellerLegalEntityComposer.ToSnapshotJson(company, imagesFrozen: true), frozen.SellerSnapshotJson);
        Assert.Empty(await target.ClientContracts.Where(c => c.Contract.Number == "KEEP-C").ToArrayAsync());
        Assert.Equal(321m, (await target.Payments.SingleAsync(p => p.Description == "Keep issued invoice")).Amount);
        Assert.Equal(1, await target.CompanyBrandImageVersions.CountAsync(i => i.LegalEntityId != company.Id));
        Assert.Equal(mode is "pending" or "legacy" or "absence" ? 1 : 4, await target.CompanyBrandImageVersions.CountAsync());
        Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, await target.SystemSettings.Where(s => s.Key == DocumentSellerSnapshotPolicy.SettingKey).Select(s => s.Value).SingleAsync());
        Assert.Empty(await target.MoneyOperations.ToArrayAsync()); Assert.Empty(await target.TaskJournalItems.ToArrayAsync());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("image-kind")]
    [InlineData("image-owner")]
    [InlineData("missing-image")]
    [InlineData("branch-owner")]
    [InlineData("currency")]
    [InlineData("duplicate-bank")]
    [InlineData("unregistered-company")]
    [InlineData("branch-conflict")]
    [InlineData("bad-json")]
    [InlineData("snapshot-owner")]
    [InlineData("missing-owner")]
    [InlineData("unknown-policy")]
    [InlineData("oncreate-without-snapshot")]
    [InlineData("archived-bank")]
    [InlineData("archived-branch")]
    [InlineData("missing-image-table")]
    public async Task HistoryRemap_RefusesInvalidPlanBeforeAnyTargetWrites(string corruption)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-refuse-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path, corruption is "archived-bank" or "archived-branch" ? "pending" : "first-file");
            await using var targetConnection = await CreateOpenConnectionAsync();
            await using (var db = CreateContext(targetConnection)) await SeedHistoryTargetAsync(db);
            await using (var connection = HistoryFile(path))
            {
                await connection.OpenAsync(); await using var source = CreateContext(connection);
                var payment = await source.Payments.SingleAsync();
                switch (corruption)
                {
                    case "hash": (await source.CompanyBrandImageVersions.FirstAsync()).Sha256 = new string('a', 64); break;
                    case "image-kind": (await source.CompanyBrandImageVersions.FirstAsync()).Kind = "wrong-kind"; break;
                    case "image-owner": (await source.CompanyBrandImageVersions.FirstAsync()).LegalEntityId = 999; break;
                    case "missing-image": source.Remove(await source.CompanyBrandImageVersions.FirstAsync()); break;
                    case "branch-owner": (await source.CompanyBranches.SingleAsync()).LegalEntityId = 999; break;
                    case "bad-json": payment.SellerSnapshotJson = "{broken"; break;
                    case "snapshot-owner": var json = JsonNode.Parse(payment.SellerSnapshotJson!)!; json["Id"] = 999; payment.SellerSnapshotJson = json.ToJsonString(); break;
                    case "missing-owner": payment.SellerCompanyId = null; break;
                    case "unknown-policy": payment.SellerSnapshotPolicy = "unknown"; break;
                    case "oncreate-without-snapshot": payment.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate; payment.SellerSnapshotJson = null; break;
                    case "archived-branch": (await source.CompanyBranches.SingleAsync()).Status = CompanyProfileStatuses.Archived; break;
                    case "missing-image-table": await source.Database.ExecuteSqlRawAsync("DROP TABLE \"CompanyBrandImageVersions\""); break;
                }
                if (corruption != "branch-owner") await source.SaveChangesAsync();
                else
                {
                    var other = new LegalEntity { Id = 999, ShortName = "Other source owner" }; source.Add(other); await source.SaveChangesAsync();
                }
            }
            await using (var target = CreateContext(targetConnection))
            {
                var company = await target.LegalEntities.SingleAsync(c => c.Inn == HistoryInn);
                if (corruption == "currency") company.BankAccountsJson = HistoryAccounts("target-bank", "USD");
                if (corruption == "duplicate-bank") { var accounts = JsonNode.Parse(company.BankAccountsJson!)!.AsArray(); var duplicate = accounts[0]!.DeepClone(); duplicate["id"] = "duplicate"; accounts.Add(duplicate); company.BankAccountsJson = accounts.ToJsonString(); }
                if (corruption == "unregistered-company") (await target.SystemSettings.SingleAsync(s => s.Key == "legalEntities.mainCompanyIds")).Value = "[1]";
                if (corruption == "branch-conflict") target.CompanyBranches.Add(new CompanyBranch { LegalEntityId = company.Id, Name = "Historical office", Code = "BR-HISTORY", Address = "Different", BankAccountId = "target-bank" });
                if (corruption == "archived-bank") { var accounts = JsonNode.Parse(company.BankAccountsJson!)!.AsArray(); accounts[0]!["isArchived"] = true; company.BankAccountsJson = accounts.ToJsonString(); }
                await target.SaveChangesAsync();
            }
            var bytes = await File.ReadAllBytesAsync(path);
            await using var before = CreateContext(targetConnection);
            var audits = await before.ObjectAuditEvents.CountAsync(); var branches = await before.CompanyBranches.CountAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(targetConnection).ImportSqliteAsync(path));
            await using var after = CreateContext(targetConnection);
            Assert.Equal(audits, await after.ObjectAuditEvents.CountAsync()); Assert.Equal(branches, await after.CompanyBranches.CountAsync());
            Assert.Equal(1, await after.Payments.CountAsync()); Assert.Equal(1, await after.CompanyBrandImageVersions.CountAsync());
            Assert.Empty(await after.Roles.ToArrayAsync()); Assert.Empty(await after.TaskJournalItems.ToArrayAsync());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { TryDeleteFile(path); }
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("act")]
    [InlineData("contract")]
    public async Task HistoryRemap_ExistingIssuedOrDeletedCollisionNeverChangesTheTarget(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-issued-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path); await using var targetConnection = await CreateOpenConnectionAsync();
            await using (var target = CreateContext(targetConnection))
            {
                await SeedHistoryTargetAsync(target);
                if (kind == "invoice")
                {
                    var client = await target.Clients.SingleAsync(); client.Name = "History client";
                    var payment = await target.Payments.Include(p => p.Invoice).SingleAsync(); payment.Amount = 100m; payment.Date = payment.PlannedDate = new DateTime(2026, 4, 15); payment.Invoice!.Number = "HIST-I"; payment.Invoice.Date = new DateTime(2026, 10, 1);
                    var branch = new CompanyBranch { LegalEntityId = payment.SellerCompanyId!.Value, Name = "Historical office", Code = "BR-HISTORY", Kpp = "770101001", BankAccountId = "target-bank" };
                    target.CompanyBranches.Add(branch); await target.SaveChangesAsync(); payment.SellerBranchId = branch.Id;
                    // Same document identity is rejected; no guessing that a matching number is safe.
                }
                if (kind == "act") { var act = await target.Acts.IgnoreQueryFilters().SingleAsync(); act.Number = "HIST-A"; act.Date = new DateTime(2026, 10, 1); }
                if (kind == "contract") { var contract = await target.Contracts.SingleAsync(); contract.Number = "HIST-C"; contract.Date = new DateTime(2026, 10, 1); }
                await target.SaveChangesAsync();
            }
            await using var before = CreateContext(targetConnection); var snapshots = new[] { (await before.Payments.SingleAsync()).SellerSnapshotJson, (await before.Acts.IgnoreQueryFilters().SingleAsync()).SellerSnapshotJson, (await before.Contracts.SingleAsync()).SellerSnapshotJson };
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(targetConnection).ImportSqliteAsync(path));
            await using var after = CreateContext(targetConnection);
            Assert.Equal(snapshots, new[] { (await after.Payments.SingleAsync()).SellerSnapshotJson, (await after.Acts.IgnoreQueryFilters().SingleAsync()).SellerSnapshotJson, (await after.Contracts.SingleAsync()).SellerSnapshotJson });
            Assert.Equal(1, await after.CompanyBrandImageVersions.CountAsync());
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public async Task HistoryRemap_ReimportRefusesWithoutDuplicateVersionsOrBranches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-repeat-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path); await using var connection = await CreateOpenConnectionAsync();
            await using (var db = CreateContext(connection)) await SeedHistoryTargetAsync(db);
            Assert.True((await CreateService(connection).ImportSqliteAsync(path)).Success);
            await using var before = CreateContext(connection); var audits = await before.ObjectAuditEvents.CountAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(connection).ImportSqliteAsync(path));
            await using var after = CreateContext(connection);
            Assert.Equal(4, await after.CompanyBrandImageVersions.CountAsync()); Assert.Equal(2, await after.CompanyBranches.CountAsync()); Assert.Equal(2, await after.Payments.CountAsync());
            Assert.Equal(audits, await after.ObjectAuditEvents.CountAsync());
        }
        finally { TryDeleteFile(path); }
    }

    private sealed class FailHistoryDocuments(CancellationTokenSource? cancellation = null) : SaveChangesInterceptor
    {
        public bool SawMaterializedReferences { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (PaymentContext)eventData.Context!;
            if (db.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Added && e.Entity.Description == "Historical invoice"))
            {
                SawMaterializedReferences = db.CompanyBrandImageVersions.Count() == 4 && db.CompanyBranches.Count() == 2;
                if (cancellation is not null) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new InvalidOperationException("Synthetic failure after materializing history");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task HistoryRemap_ReusesReferencesAndDoesNotCopyUnreferencedImages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-reuse-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path); await using var connection = await CreateOpenConnectionAsync();
            await using (var sourceConnection = HistoryFile(path))
            {
                await sourceConnection.OpenAsync(); await using var source = CreateContext(sourceConnection);
                var unused = new string('x', 1_000_000); source.CompanyBrandImageVersions.Add(new CompanyBrandImageVersion { LegalEntityId = 1, Kind = "unused", DataUrl = unused, Sha256 = HistoryHash(unused) }); await source.SaveChangesAsync();
            }
            await using (var target = CreateContext(connection))
            {
                await SeedHistoryTargetAsync(target);
                target.CompanyBranches.Add(new CompanyBranch { LegalEntityId = 2, Name = "Historical office", Code = "BR-HISTORY", BankAccountId = "target-bank", Kpp = "770101001" });
                target.CompanyBrandImageVersions.Add(new CompanyBrandImageVersion { LegalEntityId = 2, Kind = CompanyBrandImageKinds.Logo, DataUrl = HistoryImage, Sha256 = HistoryHash(HistoryImage) }); await target.SaveChangesAsync();
            }
            Assert.True((await CreateService(connection).ImportSqliteAsync(path)).Success);
            await using var verify = CreateContext(connection); await AssertHistoryResultAsync(path, verify, "first-file");
            Assert.Equal(2, await verify.CompanyBranches.CountAsync()); Assert.Equal(4, await verify.CompanyBrandImageVersions.CountAsync());
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public async Task HistoryRemap_PreservesAccountsRolesPermissionsAndAllTargetSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-security-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path); await using var connection = await CreateOpenConnectionAsync();
            await using (var sourceConnection = HistoryFile(path))
            {
                await sourceConnection.OpenAsync(); await using var source = CreateContext(sourceConnection);
                var role = await source.Roles.SingleAsync(); role.Permissions.Add(new RolePermission { Section = "acts", CanView = true, CanExport = true });
                var user = new User { Email = "history-user@example.test", FullName = "Source user", PasswordHash = "source hash", RoleId = role.Id }; source.Users.Add(user); await source.SaveChangesAsync();
                source.UserSettings.Add(new UserSetting { UserId = user.Id, Key = "history-setting", Value = "source" });
                (await source.SystemSettings.SingleAsync(s => s.Key == "notifications.email.enabled")).Value = "true"; await source.SaveChangesAsync();
            }
            await using (var target = CreateContext(connection))
            {
                await SeedHistoryTargetAsync(target);
                var role = new Role { Name = "Target role" }; role.Permissions.Add(new RolePermission { Section = "acts", CanView = false, CanExport = false });
                var user = new User { Email = "history-user@example.test", FullName = "Target user", PasswordHash = "target hash", Role = role, AuthTokenVersion = 19 }; target.Users.Add(user); await target.SaveChangesAsync();
                target.UserSettings.Add(new UserSetting { UserId = user.Id, Key = "history-setting", Value = "target" }); await target.SaveChangesAsync();
            }
            Assert.True((await CreateService(connection).ImportSqliteAsync(path)).Success);
            await using var verify = CreateContext(connection); var current = await verify.Users.Include(u => u.Role).ThenInclude(r => r!.Permissions).SingleAsync();
            Assert.Equal("Target user", current.FullName); Assert.Equal("target hash", current.PasswordHash); Assert.Equal(19, current.AuthTokenVersion); Assert.Equal("Target role", current.Role!.Name);
            Assert.False(current.Role.Permissions.Single().CanView); Assert.False(current.Role.Permissions.Single().CanExport);
            Assert.Equal("target", await verify.UserSettings.Select(s => s.Value).SingleAsync()); Assert.Equal("false", await verify.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
            Assert.Equal(1, await verify.Roles.CountAsync());
        }
        finally { TryDeleteFile(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRemap_ConflictingExistingCounterpartyRefusesWithoutChangingIssuedData(bool company)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-party-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path); await using var connection = await CreateOpenConnectionAsync();
            await using (var target = CreateContext(connection)) await SeedHistoryTargetAsync(target);
            await using (var sourceConnection = HistoryFile(path))
            {
                await sourceConnection.OpenAsync(); await using var source = CreateContext(sourceConnection);
                if (company) source.LegalEntities.Add(new LegalEntity { ShortName = "Changed counterparty", Inn = "9900000000" });
                else (await source.Clients.SingleAsync()).Email = "shared@example.test";
                await source.SaveChangesAsync();
            }
            if (!company) { await using var target = CreateContext(connection); (await target.Clients.SingleAsync()).Email = "shared@example.test"; await target.SaveChangesAsync(); }
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(connection).ImportSqliteAsync(path));
            await using var verify = CreateContext(connection);
            Assert.Equal("Keep issued owner", (await verify.Clients.SingleAsync()).Name); Assert.Equal("Target sentinel", (await verify.LegalEntities.SingleAsync(c => c.Inn == "9900000000")).ShortName);
            Assert.Equal(1, await verify.Payments.CountAsync()); Assert.Equal(1, await verify.CompanyBrandImageVersions.CountAsync());
        }
        finally { TryDeleteFile(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRemap_FailureOrCancellationAfterAssetsRollsBackEveryTable(bool cancel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-rollback-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedHistorySourceAsync(path); await using var connection = await CreateOpenConnectionAsync();
            await using (var db = CreateContext(connection)) await SeedHistoryTargetAsync(db);
            using var cancellation = new CancellationTokenSource(); var fail = new FailHistoryDocuments(cancel ? cancellation : null);
            var options = new DbContextOptionsBuilder<PaymentContext>(CreateOptions(connection)).AddInterceptors(fail).Options;
            var factory = new TestPaymentContextFactory(options, new Microsoft.AspNetCore.Http.HttpContextAccessor());
            var service = new DatabaseImportService(factory, new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, connection.ConnectionString), NullLogger<DatabaseImportService>.Instance);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportSqliteAsync(path, cancellation.Token));
            else Assert.False((await service.ImportSqliteAsync(path)).Success);
            Assert.True(fail.SawMaterializedReferences);
            await using var verify = CreateContext(connection);
            Assert.Equal(1, await verify.CompanyBrandImageVersions.CountAsync()); Assert.Equal(1, await verify.CompanyBranches.CountAsync()); Assert.Equal(1, await verify.Payments.CountAsync());
            Assert.Equal(1, await verify.Contracts.CountAsync()); Assert.Empty(await verify.Roles.ToArrayAsync()); Assert.Empty(await verify.MoneyOperations.ToArrayAsync());
        }
        finally { TryDeleteFile(path); }
    }
}
