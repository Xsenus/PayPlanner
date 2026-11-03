using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task SeedMergeBankHistoryAsync(PaymentContext db, MergeSeed seed, string history, bool primary = false)
    {
        var company = primary ? seed.Input.PrimarySourceId : seed.Input.DuplicateSourceId;
        const string profile = "[{\"id\":\"local-id\",\"accountNumber\":\"40702810200000000001\",\"bik\":\"044525225\",\"currency\":\"RUB\"}]";
        await db.LegalEntities.Where(x => x.Id == company).ExecuteUpdateAsync(s => s.SetProperty(x => x.BankAccountsJson, profile));
        var connection = new BankConnection { LegalEntityId = company, Provider = BankProviderKeys.Sber, Status = history == "disconnected" ? BankConnectionStatuses.Disconnected : BankConnectionStatuses.Active, CreatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) }; db.BankConnections.Add(connection); await db.SaveChangesAsync();
        if (history is "active" or "revoked")
        {
            var bank = new BankConnectionAccount { BankConnectionId = connection.Id, ExternalAccountId = "synthetic-source", AccountNumber = "40702810200000000001", Bik = "044525225", Currency = "RUB" }; db.BankConnectionAccounts.Add(bank); await db.SaveChangesAsync();
            var binding = CompanyBankAccountBindingStorageTests.Binding(new(company, bank.Id, seed.Actor.Id));
            if (history == "revoked") { binding.RevokedAtUtc = binding.CreatedAtUtc.AddMinutes(1); binding.RevokedByUserId = seed.Actor.Id; binding.RevokedByUserName = "Synthetic actor"; binding.RevocationMatchVersion = "revoked-synthetic-v1"; }
            db.CompanyBankAccountBindings.Add(binding); await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
    }
    private static async Task<string> MergeBankSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Bindings = await db.CompanyBankAccountBindings.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Connections = await db.BankConnections.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.LegalEntityId, x.Provider, x.Status, x.AccessTokenProtected, x.RefreshTokenProtected, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(),
        Accounts = await db.BankConnectionAccounts.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankConnectionId, x.ExternalAccountId, x.AccountNumber, x.Bik, x.Currency, x.Status }).ToArrayAsync(),
        Profiles = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankAccountsJson }).ToArrayAsync(),
    });
    public static IEnumerable<object[]> MergeBankHistoryCases() => from kind in new[] { "legal", "external+legal" } from history in new[] { "active", "revoked", "connection", "disconnected" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, history, execute, postgres };
    [Theory]
    [MemberData(nameof(MergeBankHistoryCases))]
    public async Task MergeBankBoundary_RejectsMovingCurrentOrHistoricalBankIdentity(string kind, string history, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); await SeedMergeBankHistoryAsync(db, seed, history); var before = await MergeSnapshotAsync(db); var banks = await MergeBankSnapshotAsync(db); var result = await RunMergeAsync(db, seed, execute); var bindingOwner = await db.CompanyBankAccountBindings.Select(x => (int?)x.LegalEntityId).SingleOrDefaultAsync(); var connectionOwner = await db.BankConnections.Select(x => x.LegalEntityId).SingleAsync();
        if (execute) Assert.True(result is ConflictObjectResult, $"kind={kind}; history={history}; result={result?.GetType().Name}; bindingOwner={bindingOwner}; connectionOwner={connectionOwner}; archived={!await db.LegalEntities.AnyAsync(x => x.Id == seed.Input.DuplicateSourceId)}");
        else { var reply = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(result).Value); Assert.False(reply.CanMerge); Assert.Contains(reply.Conflicts, x => x.Contains("банков", StringComparison.OrdinalIgnoreCase)); }
        Assert.Equal(before, await MergeSnapshotAsync(db)); Assert.Equal(banks, await MergeBankSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }
    public static IEnumerable<object[]> MergeBankPrimaryCases() => from kind in new[] { "legal", "legal+external" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, execute, postgres };
    [Theory]
    [MemberData(nameof(MergeBankPrimaryCases))]
    public async Task MergeBankBoundary_HistoryOnUnchangedPrimaryDoesNotBlock(string kind, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); await SeedMergeBankHistoryAsync(db, seed, "active", primary: true); var banks = await MergeBankSnapshotAsync(db); var reply = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, execute)).Value); Assert.True(reply.CanMerge); Assert.Equal(banks, await MergeBankSnapshotAsync(db));
    }
    public static IEnumerable<object[]> MergeBankNoHistoryCases() => from kind in new[] { "legal", "external+legal" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, execute, postgres };
    [Theory]
    [MemberData(nameof(MergeBankNoHistoryCases))]
    public async Task MergeBankBoundary_UnconnectedCompaniesRemainMergeable(string kind, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); var reply = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, execute)).Value); Assert.True(reply.CanMerge); Assert.Empty(await db.BankConnections.ToArrayAsync()); Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync());
    }
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgresql-model")]
    public async Task MergeBankBoundary_ReferenceModelInventory(string provider)
    {
        await using PaymentContext db = provider == "sqlite" ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").Options) : new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql("Host=127.0.0.1;Port=5433;Database=postgres;Username=payplanner_ci").Options);
        var principals = new[] { typeof(Client), typeof(LegalEntity), typeof(ExternalContact) };
        var inventory = db.Model.GetEntityTypes().Where(entity => entity.GetTableName() is not null).SelectMany(entity => entity.GetForeignKeys().Where(fk => principals.Contains(fk.PrincipalEntityType.ClrType) && fk.Properties.Count == 1 && fk.PrincipalKey.Properties.Count == 1 && fk.PrincipalKey.Properties[0].Name == "Id").Select(fk => new { Principal = fk.PrincipalEntityType.ClrType.Name, Entity = entity.ClrType.Name, Table = entity.GetTableName(), Schema = entity.GetSchema(), Property = fk.Properties[0].Name, Column = fk.Properties[0].GetColumnName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema())), Delete = fk.DeleteBehavior.ToString(), Shadow = fk.Properties[0].IsShadowProperty(), Owned = entity.IsOwned() })).OrderBy(x => x.Principal).ThenBy(x => x.Table).ThenBy(x => x.Column).ToArray();
        Assert.Contains(inventory, x => x.Principal == nameof(LegalEntity) && x.Entity == nameof(CompanyBankAccountBinding)); Assert.DoesNotContain(inventory, x => x.Entity == nameof(BankConnection)); Assert.DoesNotContain(inventory, x => x.Entity == nameof(CompanyBrandImageVersion)); Assert.Contains(inventory, x => x.Principal == nameof(Client) && x.Entity == nameof(ClientPassport)); Console.WriteLine("MERGE_REFERENCE_INVENTORY=" + JsonSerializer.Serialize(new { Provider = provider, References = inventory }));
    }
    [Theory]
    [InlineData("client", false)]
    [InlineData("client", true)]
    [InlineData("external+client", false)]
    [InlineData("external+client", true)]
    public async Task MergeBankBoundary_EncryptedPassportSurvivesOwnerTransfer(string kind, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); var passport = new ClientPassport { ClientId = seed.Input.DuplicateSourceId, IsPrimary = true, Series = "TEST", Number = "1111", IssuedBy = "Synthetic issuer", IssuedAt = new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc), DepartmentCode = "000-000", BirthPlacePassport = "Synthetic place", BirthPlaceFias = "synthetic-fias" }; db.ClientPassports.Add(passport); await db.SaveChangesAsync();
        var encryptor = new AeadFieldEncryptor(new MergePassportTestKeyStore()); var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FieldEncryption:Enabled"] = "true", ["FieldEncryption:ClientPassportDualWriteEnabled"] = "true" }).Build(); var writer = new ClientPassportEncryptionDualWriteService(encryptor, config); await writer.ProtectAsync(passport, null, default); await db.SaveChangesAsync(); var fields = new[] { "Series", "Number", "IssuedBy", "IssuedAt", "DepartmentCode", "BirthPlacePassport", "BirthPlaceFias" }; string?[] Ciphers(ClientPassport x) => [x.SeriesEncrypted, x.NumberEncrypted, x.IssuedByEncrypted, x.IssuedAtEncrypted, x.DepartmentCodeEncrypted, x.BirthPlacePassportEncrypted, x.BirthPlaceFiasEncrypted]; var before = Ciphers(passport); Assert.All(before, x => Assert.NotNull(x)); var indexes = new[] { passport.SeriesBlindIndex, passport.NumberBlindIndex }; var clear = new List<string>(); for (var i = 0; i < fields.Length; i++) clear.Add(await encryptor.DecryptAsync(before[i]!, $"ClientPassport:{fields[i]}:{passport.Id}", default));
        Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, true)); var saved = await db.ClientPassports.AsNoTracking().SingleAsync(x => x.Id == passport.Id); Assert.NotEqual(seed.Input.DuplicateSourceId, saved.ClientId); var expected = kind == "client" ? seed.Input.PrimarySourceId : (await db.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == seed.Input.PrimarySourceId)).LinkedClientId; Assert.Equal(expected, saved.ClientId); Assert.Equal(before, Ciphers(saved)); Assert.Equal(indexes, new[] { saved.SeriesBlindIndex, saved.NumberBlindIndex }); for (var i = 0; i < fields.Length; i++) Assert.Equal(clear[i], await encryptor.DecryptAsync(Ciphers(saved)[i]!, $"ClientPassport:{fields[i]}:{saved.Id}", default)); Assert.Equal("1111", saved.Number);
    }
    // Synthetic in-memory material only; no production/environment key store is used.
    private sealed class MergePassportTestKeyStore : IFieldEncryptionKeyStore
    {
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync("merge-test-only-v1", ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct) => keyId == "merge-test-only-v1" ? ValueTask.FromResult(new FieldEncryptionKeyMaterial(keyId, Enumerable.Repeat((byte)17, 32).ToArray(), Enumerable.Repeat((byte)18, 32).ToArray())) : throw new InvalidOperationException("Unknown synthetic key.");
    }
}
