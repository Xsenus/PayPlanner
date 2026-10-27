using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class DatabaseImportServiceTests
{
    [Fact]
    public async Task OlderSqliteWithoutPolicyColumns_ImportsAsLegacyWithoutAlteringSourceOrTargetModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-seller-legacy-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedSellerSourceAsync(path);
            await using (var source = SellerFileConnection(path))
            {
                await source.OpenAsync();
                foreach (var table in new[] { "Payments", "Acts", "Contracts" })
                {
                    await using var command = source.CreateCommand();
                    command.CommandText = $"ALTER TABLE \"{table}\" DROP COLUMN \"SellerSnapshotPolicy\"";
                    await command.ExecuteNonQueryAsync();
                }
            }
            var original = await File.ReadAllBytesAsync(path);
            await using var targetConnection = await CreateOpenConnectionAsync();
            await PrepareSellerTargetAsync(targetConnection);

            var result = await CreateService(targetConnection).ImportSqliteAsync(path);

            Assert.True(result.Success);
            await using var verify = CreateContext(targetConnection);
            Assert.Null((await verify.Payments.SingleAsync()).SellerSnapshotPolicy);
            Assert.Null((await verify.Acts.SingleAsync()).SellerSnapshotPolicy);
            Assert.Null((await verify.Contracts.SingleAsync()).SellerSnapshotPolicy);
            Assert.NotNull(verify.Model.FindEntityType(typeof(Payment))!.FindProperty(nameof(Payment.SellerSnapshotPolicy)));
            Assert.NotNull(verify.Model.FindEntityType(typeof(Act))!.FindProperty(nameof(Act.SellerSnapshotPolicy)));
            Assert.NotNull(verify.Model.FindEntityType(typeof(Contract))!.FindProperty(nameof(Contract.SellerSnapshotPolicy)));
            Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, await verify.SystemSettings.Where(setting => setting.Key == DocumentSellerSnapshotPolicy.SettingKey).Select(setting => setting.Value).SingleAsync());
            Assert.Empty(await verify.CompanyBrandImageVersions.ToArrayAsync());
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HistoryImport_PreservesExplicitFirstFilePolicyWithoutApplyingCurrentOnCreateSetting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-seller-policy-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedSellerSourceAsync(path, DocumentSellerSnapshotPolicy.FirstFile);
            await using var targetConnection = await CreateOpenConnectionAsync();
            await PrepareSellerTargetAsync(targetConnection);

            Assert.True((await CreateService(targetConnection).ImportSqliteAsync(path)).Success);

            await using var verify = CreateContext(targetConnection);
            Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, (await verify.Payments.SingleAsync()).SellerSnapshotPolicy);
            Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, (await verify.Acts.SingleAsync()).SellerSnapshotPolicy);
            Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, (await verify.Contracts.SingleAsync()).SellerSnapshotPolicy);
            Assert.Null((await verify.Payments.SingleAsync()).SellerSnapshotJson);
            Assert.Null((await verify.Acts.SingleAsync()).SellerSnapshotJson);
            Assert.Null((await verify.Contracts.SingleAsync()).SellerSnapshotJson);
            Assert.Empty(await verify.CompanyBrandImageVersions.ToArrayAsync());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Payments", "unknown")]
    [InlineData("Acts", "unknown")]
    [InlineData("Contracts", "unknown")]
    [InlineData("Payments", DocumentSellerSnapshotPolicy.OnCreate)]
    [InlineData("Acts", DocumentSellerSnapshotPolicy.OnCreate)]
    [InlineData("Contracts", DocumentSellerSnapshotPolicy.OnCreate)]
    public async Task UnsupportedSourcePolicy_RejectsBeforeAnyImportedReferenceOrBusinessWrite(string table, string policy)
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-seller-invalid-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedSellerSourceAsync(path);
            await using (var source = SellerFileConnection(path))
            {
                await source.OpenAsync();
                await using var command = source.CreateCommand();
                command.CommandText = $"UPDATE \"{table}\" SET \"SellerSnapshotPolicy\" = @policy";
                command.Parameters.AddWithValue("policy", policy);
                await command.ExecuteNonQueryAsync();
            }
            await using var targetConnection = await CreateOpenConnectionAsync();
            await PrepareSellerTargetAsync(targetConnection);
            await using var before = CreateContext(targetConnection);
            var audit = await before.ObjectAuditEvents.CountAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(targetConnection).ImportSqliteAsync(path));

            Assert.Contains("до записи", error.Message);
            await AssertSellerImportUnchangedAsync(targetConnection, audit);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Payments")]
    [InlineData("Acts")]
    [InlineData("Contracts")]
    public async Task SourceImmutableSnapshotReferences_RejectBeforeAnyWrite(string table)
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-seller-refs-{Guid.NewGuid():N}.sqlite");
        try
        {
            await SeedSellerSourceAsync(path);
            await using (var source = SellerFileConnection(path))
            {
                await source.OpenAsync();
                await using var command = source.CreateCommand();
                command.CommandText = $"UPDATE \"{table}\" SET \"SellerSnapshotJson\" = @snapshot";
                command.Parameters.AddWithValue("snapshot", "{\"Id\":123,\"ShortName\":\"Frozen source\",\"ImagesFrozen\":true,\"LogoVersionId\":456}");
                await command.ExecuteNonQueryAsync();
            }
            await using var targetConnection = await CreateOpenConnectionAsync();
            await PrepareSellerTargetAsync(targetConnection);
            await using var before = CreateContext(targetConnection);
            var audit = await before.ObjectAuditEvents.CountAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(targetConnection).ImportSqliteAsync(path));

            await AssertSellerImportUnchangedAsync(targetConnection, audit);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingTargetIssuedAct_IsNotClearedEvenWhenSoftDeleted(bool deleted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-seller-target-{Guid.NewGuid():N}.sqlite");
        const string snapshot = "{\"Id\":123,\"ShortName\":\"Issued target\",\"ImagesFrozen\":true,\"LogoVersionId\":456}";
        try
        {
            await SeedSellerSourceAsync(path);
            await using var targetConnection = await CreateOpenConnectionAsync();
            await PrepareSellerTargetAsync(targetConnection);
            await using (var target = CreateContext(targetConnection))
            {
                var act = new Act
                {
                    Number = "SELLER-ACT",
                    Date = new DateTime(2026, 10, 24),
                    Amount = 100m,
                    SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate,
                    SellerSnapshotJson = snapshot
                };
                target.Acts.Add(act);
                await target.SaveChangesAsync();
                if (deleted)
                {
                    target.Entry(act).Property("IsDeleted").CurrentValue = true;
                    await target.SaveChangesAsync();
                }
            }
            await using var before = CreateContext(targetConnection);
            var audit = await before.ObjectAuditEvents.CountAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(targetConnection).ImportSqliteAsync(path));

            await using var verify = CreateContext(targetConnection);
            var retained = await verify.Acts.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(snapshot, retained.SellerSnapshotJson);
            Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, retained.SellerSnapshotPolicy);
            Assert.Equal(deleted, verify.Entry(retained).Property<bool>("IsDeleted").CurrentValue);
            Assert.Empty(await verify.Clients.ToArrayAsync());
            Assert.Empty(await verify.Contracts.ToArrayAsync());
            Assert.Empty(await verify.Payments.ToArrayAsync());
            Assert.Equal(audit, await verify.ObjectAuditEvents.CountAsync());
        }
        finally { File.Delete(path); }
    }

    private static SqliteConnection SellerFileConnection(string path)
        => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());

    private static async Task PrepareSellerTargetAsync(SqliteConnection connection)
    {
        await using var target = CreateContext(connection);
        await target.Database.EnsureCreatedAsync();
        target.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
        await target.SaveChangesAsync();
    }

    private static async Task SeedSellerSourceAsync(string path, string? policy = null)
    {
        await using var connection = SellerFileConnection(path);
        await connection.OpenAsync();
        await using var source = CreateContext(connection);
        await source.Database.EnsureCreatedAsync();
        var client = new Client { Name = "Source seller client" };
        var invoice = new Invoice { Number = "SELLER-INVOICE", Date = new DateTime(2026, 10, 24) };
        var payment = CreateInvoicePayment(invoice, client, null, "Historical imported invoice");
        payment.SellerSnapshotPolicy = policy;
        source.Payments.Add(payment);
        source.Acts.Add(new Act { Number = "SELLER-ACT", Date = invoice.Date, Client = client, Amount = 100m, SellerSnapshotPolicy = policy });
        source.Contracts.Add(new Contract
        {
            Number = "SELLER-CONTRACT",
            Date = invoice.Date,
            StartDate = invoice.Date,
            Amount = 100m,
            IsOpenEnded = true,
            SellerSnapshotPolicy = policy
        });
        source.Roles.Add(new Role { Name = "Source role must not be written on refusal" });
        await source.SaveChangesAsync();
    }

    private static async Task AssertSellerImportUnchangedAsync(SqliteConnection connection, int expectedAudit)
    {
        await using var verify = CreateContext(connection);
        Assert.Empty(await verify.Clients.ToArrayAsync());
        Assert.Empty(await verify.Contracts.ToArrayAsync());
        Assert.Empty(await verify.Payments.ToArrayAsync());
        Assert.Empty(await verify.Acts.ToArrayAsync());
        Assert.Empty(await verify.Roles.ToArrayAsync());
        Assert.Empty(await verify.CompanyBrandImageVersions.ToArrayAsync());
        Assert.Equal(expectedAudit, await verify.ObjectAuditEvents.CountAsync());
    }
}
