using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientPortalServiceTests
{
    [Fact]
    public async Task Feature_is_safely_disabled()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        var error = await Assert.ThrowsAsync<ClientPortalException>(() => fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id));
        Assert.Equal("feature_disabled", error.Code);
        Assert.Empty(await fixture.Db.ClientPortalAccounts.ToListAsync());
    }

    [Fact]
    public async Task Magic_link_and_otp_are_single_use_and_create_session()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var account = await fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id);
        var challenge = await fixture.Service.SendLoginAsync(account.Id, CancellationToken.None);
        var preview = await fixture.Service.PreviewLoginAsync(challenge.ChallengeId, fixture.Email.Token, CancellationToken.None);
        Assert.Equal("Клиент 1", preview.DisplayName);

        var session = await fixture.Service.VerifyLoginAsync(
            challenge.ChallengeId, fixture.Email.Token, fixture.Email.Otp, "127.0.0.1", "test", CancellationToken.None);
        Assert.NotNull(await fixture.Service.ResolveSessionAsync(session.SessionToken, null, null, CancellationToken.None));

        var replay = await Assert.ThrowsAsync<ClientPortalException>(() =>
            fixture.Service.VerifyLoginAsync(challenge.ChallengeId, fixture.Email.Token, fixture.Email.Otp, null, null, CancellationToken.None));
        Assert.Equal("link_unavailable", replay.Code);
    }

    [Fact]
    public async Task Five_invalid_codes_lock_challenge()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var account = await fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id);
        var challenge = await fixture.Service.SendLoginAsync(account.Id, CancellationToken.None);
        var badOtp = fixture.Email.Otp == "000000" ? "999999" : "000000";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<ClientPortalException>(() =>
                fixture.Service.VerifyLoginAsync(challenge.ChallengeId, fixture.Email.Token, badOtp, null, null, CancellationToken.None));
        }

        Assert.NotNull((await fixture.Db.ClientPortalLoginChallenges.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task Account_cannot_enumerate_another_clients_case()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var first = await fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id);
        var second = await fixture.CreateAccessAsync(2, fixture.Client2Id, fixture.Case2Id);
        var firstSession = new ClientPortalSessionContext(first.Id, first.PublicId, first.DisplayName, first.Email);

        var ownGrant = first.Grants.Single().CasePublicId;
        var otherGrant = second.Grants.Single().CasePublicId;
        Assert.NotNull(await fixture.Service.GetCaseAsync(firstSession, ownGrant, CancellationToken.None));
        Assert.Null(await fixture.Service.GetCaseAsync(firstSession, otherGrant, CancellationToken.None));
        Assert.Single((await fixture.Service.GetHomeAsync(firstSession, 1, 20, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task Client_access_must_match_case_owner_and_revocation_invalidates_sessions()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var mismatch = await Assert.ThrowsAsync<ClientPortalException>(() =>
            fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case2Id));
        Assert.Equal("access_mismatch", mismatch.Code);

        var account = await fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id);
        var challenge = await fixture.Service.SendLoginAsync(account.Id, CancellationToken.None);
        var session = await fixture.Service.VerifyLoginAsync(
            challenge.ChallengeId, fixture.Email.Token, fixture.Email.Otp, null, null, CancellationToken.None);
        await fixture.Service.RevokeAccountAsync(account.Id, 7, "Администратор", "Тест", CancellationToken.None);
        Assert.Null(await fixture.Service.ResolveSessionAsync(session.SessionToken, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_one_representative_grant_keeps_other_case_and_session()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var request = new ClientPortalAccessRequest
        {
            Email = "representative@example.test",
            DisplayName = "Представитель",
            AccountType = "representative",
            ClientCaseId = fixture.Case1Id,
            CanViewCaseSummary = true,
        };
        var account = await fixture.Service.CreateOrUpdateAccessAsync(request, 7, "Администратор", CancellationToken.None);
        request.ClientCaseId = fixture.Case2Id;
        account = await fixture.Service.CreateOrUpdateAccessAsync(request, 7, "Администратор", CancellationToken.None);
        var challenge = await fixture.Service.SendLoginAsync(account.Id, CancellationToken.None);
        var session = await fixture.Service.VerifyLoginAsync(
            challenge.ChallengeId, fixture.Email.Token, fixture.Email.Otp, null, null, CancellationToken.None);

        await fixture.Service.RevokeCaseAccessAsync(account.Id, fixture.Case1Id, 7, "Тест", CancellationToken.None);

        var context = await fixture.Service.ResolveSessionAsync(session.SessionToken, null, null, CancellationToken.None);
        Assert.NotNull(context);
        var home = await fixture.Service.GetHomeAsync(context!, 1, 20, CancellationToken.None);
        Assert.Single(home.Items);
        Assert.Equal("Дело 2", home.Items[0].Title);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, PaymentContext db, RecordingEmailService email, ClientPortalService service)
        {
            _connection = connection;
            Db = db;
            Email = email;
            Service = service;
        }

        public PaymentContext Db { get; }
        public RecordingEmailService Email { get; }
        public ClientPortalService Service { get; }
        public int Client1Id { get; private set; }
        public int Client2Id { get; private set; }
        public int Case1Id { get; private set; }
        public int Case2Id { get; private set; }

        public static async Task<Fixture> CreateAsync(bool enabled)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var client1 = new Client { Name = "Клиент 1", Email = "one@example.test" };
            var client2 = new Client { Name = "Клиент 2", Email = "two@example.test" };
            var case1 = new ClientCase { Client = client1, Title = "Дело 1", CaseNumber = "1" };
            var case2 = new ClientCase { Client = client2, Title = "Дело 2", CaseNumber = "2" };
            db.ClientCases.AddRange(case1, case2);
            await db.SaveChangesAsync();

            var email = new RecordingEmailService();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test" })
                .Build();
            var flags = new FixedOptionsMonitor(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions { ClientPortal = enabled },
            });
            var service = new ClientPortalService(db, email, configuration, flags, new MissingDocumentStorage());
            return new Fixture(connection, db, email, service)
            {
                Client1Id = client1.Id,
                Client2Id = client2.Id,
                Case1Id = case1.Id,
                Case2Id = case2.Id,
            };
        }

        public Task<ClientPortalAdminAccountDto> CreateAccessAsync(int suffix, int clientId, int caseId)
            => Service.CreateOrUpdateAccessAsync(new ClientPortalAccessRequest
            {
                Email = $"client{suffix}@example.test",
                DisplayName = $"Клиент {suffix}",
                AccountType = "client",
                ClientId = clientId,
                ClientCaseId = caseId,
                CanViewCaseSummary = true,
                CanViewDeadlines = true,
            }, 7, "Администратор", CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedOptionsMonitor(AppFeatureFlagsOptions value) : IOptionsMonitor<AppFeatureFlagsOptions>
    {
        public AppFeatureFlagsOptions CurrentValue { get; } = value;
        public AppFeatureFlagsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppFeatureFlagsOptions, string?> listener) => null;
    }

    private sealed class MissingDocumentStorage : ICaseDocumentStorage
    {
        public string BuildRelativePath(int caseId, string storedFileName) => $"{caseId}/{storedFileName}";
        public string ResolvePath(string storagePath) => storagePath;
        public Task SaveAsync(string storagePath, Stream content, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(string storagePath, byte[] content, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool Exists(string storagePath) => false;
        public void DeleteIfExists(string storagePath) { }
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public string Token { get; private set; } = string.Empty;
        public string Otp { get; private set; } = string.Empty;
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default, EmailSendSettings? settings = null)
        {
            Token = Uri.UnescapeDataString(Regex.Match(body, @"#token=([^""&<]+)").Groups[1].Value);
            Otp = Regex.Match(body, @">(\d{6})</strong>").Groups[1].Value;
            return Task.CompletedTask;
        }
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }
}
