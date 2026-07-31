using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Data;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ElectronicSignatureServiceTests
{
    [Fact]
    public async Task ControllerListsOnlyClientAndExplicitRepresentativeRecipients()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clientCase = new ClientCase { ClientId = fixture.ClientId, Title = "Дело клиента" };
        var direct = new ClientPortalAccount
        {
            ClientId = fixture.ClientId,
            DisplayName = "Прямой клиент",
            Email = "direct@example.test",
            AccountType = "client"
        };
        var representative = new ClientPortalAccount
        {
            DisplayName = "Явный представитель",
            Email = "representative@example.test",
            AccountType = "representative",
            CaseGrants = [new ClientPortalCaseGrant { ClientCase = clientCase, CanViewCaseSummary = true }]
        };
        var unrelated = new ClientPortalAccount
        {
            DisplayName = "Посторонний аккаунт",
            Email = "other@example.test",
            AccountType = "representative"
        };
        fixture.Db.AddRange(clientCase, direct, representative, unrelated);
        await fixture.Db.SaveChangesAsync();
        var controller = new ElectronicSignaturesController(
            fixture.Db,
            fixture.Service,
            new ConfigurationBuilder().Build());

        var result = await controller.GetRecipients(fixture.ClientId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Assert.Contains("Прямой клиент", json, StringComparison.Ordinal);
        Assert.Contains("Явный представитель", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Посторонний аккаунт", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareCreatesImmutableEncryptedSnapshotAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        var bytes = "%PDF-1.7 immutable"u8.ToArray();

        var first = await fixture.Service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var second = await fixture.Service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(ElectronicSignatureEnvelopeStatuses.Prepared, first.Status);
        Assert.Equal(2, first.Signers.Count);
        Assert.All(first.Signers, signer => Assert.DoesNotContain("@example.test", signer.ProtectedContact));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.SnapshotPath));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.ToListAsync());
        Assert.Single(await fixture.Db.ElectronicSignatureEvents.ToListAsync());
    }

    [Fact]
    public async Task ChangedVersionCreatesNewEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "%PDF-1.7 immutable"u8.ToArray();
        var first = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var second = await fixture.Service.PrepareAsync(fixture.Request(version: 2), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await fixture.Db.ElectronicSignatureEnvelopes.CountAsync());
    }

    [Fact]
    public async Task RejectsInvalidPdfAndUnsupportedDocument()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream("not a pdf"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            fixture.Request(documentType: "passport"), new MemoryStream("%PDF-1.7"u8.ToArray()), "passport.pdf", CancellationToken.None));

        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToListAsync());
    }

    [Fact]
    public async Task RejectsUnknownProviderAndSignerWithoutRequiredPowerOfAttorney()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        var unknownProvider = request with { Provider = "ManualUpload" };
        var missingMchd = request with
        {
            Signers = [new ElectronicSignatureSignerDraft(
                "Представитель",
                "representative@example.test",
                "representative",
                RequiresMachineReadablePowerOfAttorney: true)]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            unknownProvider, new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            missingMchd, new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToListAsync());
    }

    [Fact]
    public void CompletedRequiresVerifiedArtifacts()
    {
        var envelope = new ElectronicSignatureEnvelope { Status = ElectronicSignatureEnvelopeStatuses.Pending };
        Assert.Throws<InvalidOperationException>(() =>
            ElectronicSignatureStateMachine.EnsureTransition(envelope, ElectronicSignatureEnvelopeStatuses.Completed));

        envelope.Artifacts.Add(new ElectronicSignatureArtifact { VerificationStatus = "verified" });
        ElectronicSignatureStateMachine.EnsureTransition(envelope, ElectronicSignatureEnvelopeStatuses.Completed);
        Assert.False(ElectronicSignatureStateMachine.CanTransition(
            ElectronicSignatureEnvelopeStatuses.Completed,
            ElectronicSignatureEnvelopeStatuses.Pending));
    }

    [Fact]
    public async Task DisabledProviderNeverSendsDocument()
    {
        var provider = new DisabledElectronicSignatureProvider("KonturDiadoc");
        var result = await provider.CreateEnvelopeAsync(
            new ElectronicSignatureEnvelope(), new MemoryStream("%PDF-"u8.ToArray()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("не подключён", result.Error);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _root;
        public PaymentContext Db { get; }
        public ElectronicSignatureService Service { get; }
        public int UserId { get; }
        public int ClientId { get; }

        private Fixture(SqliteConnection connection, string root, PaymentContext db, ElectronicSignatureService service, int userId, int clientId)
        {
            _connection = connection;
            _root = root;
            Db = db;
            Service = service;
            UserId = userId;
            ClientId = clientId;
        }

        public ElectronicSignaturePreparation Request(int version = 1, string documentType = "invoice") => new(
            documentType,
            101,
            version,
            ClientId,
            ElectronicSignatureTypes.Qualified,
            ElectronicSignatureSigningOrders.Sequential,
            "KonturDiadoc",
            10,
            true,
            UserId,
            [
                new("Клиент", "client@example.test"),
                new("Представитель", "representative@example.test", "representative", RequiresMachineReadablePowerOfAttorney: true, PowerOfAttorneyReference: "МЧД-1")
            ]);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
            var db = new PaymentContext(options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "admin", Description = "Администратор" };
            var user = new User
            {
                Email = "admin@example.test",
                FullName = "Администратор",
                PasswordHash = "hash",
                Role = role,
                IsActive = true,
                IsApproved = true
            };
            var client = new Client { Name = "Клиент", IsActive = true };
            db.AddRange(user, client);
            await db.SaveChangesAsync();

            var root = Path.Combine(Path.GetTempPath(), $"payplanner-esign-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ElectronicSignature:PreparationEnabled"] = "true",
                ["ElectronicSignature:StorageRoot"] = Path.Combine(root, "storage")
            }).Build();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var service = new ElectronicSignatureService(db, configuration, protection, new TestEnvironment(root));
            return new Fixture(connection, root, db, service, user.Id, client.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
