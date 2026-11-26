using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
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
    public async Task Staff_preview_of_draft_permissions_works_with_client_login_disabled()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        var own = new ClientCaseDocument { ClientCaseId = fixture.Case1Id, OriginalFileName = "Клиентский документ.pdf", DocumentKey = "own" };
        var other = new ClientCaseDocument { ClientCaseId = fixture.Case2Id, OriginalFileName = "Чужой документ.pdf", DocumentKey = "other" };
        fixture.Db.ClientCaseDocuments.AddRange(own, other);
        await fixture.Db.SaveChangesAsync();

        var preview = await fixture.Service.PreviewCaseAsync(fixture.Case1Id,
            new ClientPortalCasePreviewRequest { CanViewDocuments = true, DocumentIds = [own.Id, other.Id] },
            7, "Администратор", CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("Дело 1", preview.Title);
        Assert.Equal("Клиентский документ.pdf", Assert.Single(preview.Documents).Name);
        Assert.Empty(preview.Invoices);
        Assert.Empty(await fixture.Db.ClientPortalAccounts.ToListAsync());
        Assert.Empty(await fixture.Db.ClientPortalCaseGrants.ToListAsync());
        Assert.Empty(await fixture.Db.ClientPortalLoginChallenges.ToListAsync());
        Assert.Empty(await fixture.Db.ClientPortalSessions.ToListAsync());
        Assert.Empty(fixture.Email.Subjects);
        var audit = Assert.Single(await fixture.Db.ObjectAuditEvents.Where(item => item.Action == "ClientPortalPreview").ToListAsync());
        Assert.Equal("client-case", audit.ObjectType);
        Assert.Equal(fixture.Case1Id, audit.ObjectId);
        Assert.Equal(7, audit.ActorUserId);
    }

    [Fact]
    public async Task Staff_preview_of_saved_access_enforces_the_case_grant()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var account = await fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id);
        var document = new ClientCaseDocument { ClientCaseId = fixture.Case1Id, OriginalFileName = "Внутренний документ.pdf", DocumentKey = "private" };
        fixture.Db.ClientCaseDocuments.Add(document);
        await fixture.Db.SaveChangesAsync();

        var request = new ClientPortalCasePreviewRequest { AccountId = account.Id, CanViewDocuments = true, DocumentIds = [document.Id] };
        Assert.Null(await fixture.Service.PreviewCaseAsync(fixture.Case2Id, request, 7, "Администратор", CancellationToken.None));
        var preview = await fixture.Service.PreviewCaseAsync(fixture.Case1Id, request, 7, "Администратор", CancellationToken.None);
        Assert.NotNull(preview);
        Assert.Equal(account.Grants.Single().CasePublicId, preview.PublicId);
        Assert.Empty(preview.Documents);
        await fixture.Service.RevokeCaseAccessAsync(account.Id, fixture.Case1Id, 7, null, CancellationToken.None);
        Assert.Null(await fixture.Service.PreviewCaseAsync(fixture.Case1Id, request, 7, "Администратор", CancellationToken.None));
        Assert.Single(await fixture.Db.ObjectAuditEvents.Where(item => item.Action == "ClientPortalPreview").ToListAsync());
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
        Assert.Contains("Новый вход в клиентский кабинет", fixture.Email.Subjects);

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
        Assert.Single((await fixture.Service.GetHomeAsync(firstSession, 1, 20, CancellationToken.None, "Дело 1")).Items);
        Assert.Empty((await fixture.Service.GetHomeAsync(firstSession, 1, 20, CancellationToken.None, "Дело 2")).Items);
        Assert.Single((await fixture.Service.GetHomeAsync(firstSession, 1, 20, CancellationToken.None, status: "open")).Items);
        Assert.Empty((await fixture.Service.GetHomeAsync(firstSession, 1, 20, CancellationToken.None, status: "unknown")).Items);
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

    [Fact]
    public async Task Legal_entity_client_access_must_match_legal_case_owner()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var legalEntity = new LegalEntity { ShortName = "ООО Тест" };
        var legalCase = new ClientCase { LegalEntity = legalEntity, Title = "Дело организации", CaseNumber = "Ю-1" };
        fixture.Db.ClientCases.Add(legalCase);
        await fixture.Db.SaveChangesAsync();

        var account = await fixture.Service.CreateOrUpdateAccessAsync(new ClientPortalAccessRequest
        {
            Email = "director@example.test",
            DisplayName = "Директор",
            AccountType = "client",
            LegalEntityId = legalEntity.Id,
            ClientCaseId = legalCase.Id,
            CanViewCaseSummary = true,
        }, 7, "Администратор", CancellationToken.None);
        Assert.Equal(legalEntity.Id, account.LegalEntityId);

        var mismatch = await Assert.ThrowsAsync<ClientPortalException>(() =>
            fixture.Service.CreateOrUpdateAccessAsync(new ClientPortalAccessRequest
            {
                Email = "other@example.test",
                DisplayName = "Другой клиент",
                AccountType = "client",
                ClientId = fixture.Client1Id,
                ClientCaseId = legalCase.Id,
                CanViewCaseSummary = true,
            }, 7, "Администратор", CancellationToken.None));
        Assert.Equal("access_mismatch", mismatch.Code);
    }

    [Fact]
    public async Task Document_download_requires_account_bound_short_lived_grant()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var bytes = "portal-document"u8.ToArray();
        var storedName = $"{Guid.NewGuid():N}.pdf";
        var storagePath = fixture.Storage.BuildRelativePath(fixture.Case1Id, storedName);
        await fixture.Storage.SaveAsync(storagePath, bytes, CancellationToken.None);
        var document = new ClientCaseDocument
        {
            ClientCaseId = fixture.Case1Id,
            DocumentKey = Guid.NewGuid().ToString("N"),
            OriginalFileName = "Документ.pdf",
            StoredFileName = storedName,
            StoragePath = storagePath,
            ContentType = "application/pdf",
            SizeBytes = bytes.LongLength,
            DocumentType = "Документ",
            Status = "Готов",
            UploadedByName = "Тест",
        };
        fixture.Db.ClientCaseDocuments.Add(document);
        await fixture.Db.SaveChangesAsync();
        var account = await fixture.Service.CreateOrUpdateAccessAsync(new ClientPortalAccessRequest
        {
            Email = "documents@example.test",
            DisplayName = "Клиент с документом",
            AccountType = "client",
            ClientId = fixture.Client1Id,
            ClientCaseId = fixture.Case1Id,
            CanViewCaseSummary = true,
            CanViewDocuments = true,
            DocumentIds = [document.Id],
        }, 7, "Администратор", CancellationToken.None);
        var context = new ClientPortalSessionContext(account.Id, account.PublicId, account.DisplayName, account.Email);
        var publicDocumentId = await fixture.Db.ClientPortalDocumentGrants.Select(item => item.PublicId).SingleAsync();

        var grant = await fixture.Service.CreateDocumentGrantAsync(context, publicDocumentId, CancellationToken.None);
        Assert.NotNull(grant);
        var download = await fixture.Service.GetDocumentAsync(context, publicDocumentId, grant!.GrantToken, CancellationToken.None);
        Assert.NotNull(download);
        Assert.Equal("portal-document", await File.ReadAllTextAsync(download!.FullPath));

        var otherContext = context with { AccountId = account.Id + 100 };
        Assert.Null(await fixture.Service.GetDocumentAsync(otherContext, publicDocumentId, grant.GrantToken, CancellationToken.None));
        Assert.Null(await fixture.Service.GetDocumentAsync(context, publicDocumentId, grant.GrantToken + "x", CancellationToken.None));
    }

    [Fact]
    public async Task Backup_restore_preserves_revoked_access_and_audit_history()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var account = await fixture.CreateAccessAsync(1, fixture.Client1Id, fixture.Case1Id);
        await fixture.Service.RevokeAccountAsync(account.Id, 7, "Администратор", "Проверка хранения", CancellationToken.None);

        await using var restored = await fixture.BackupAndRestoreAsync();

        var restoredAccount = await restored.ClientPortalAccounts.AsNoTracking().SingleAsync();
        Assert.False(restoredAccount.IsActive);
        Assert.NotNull(restoredAccount.RevokedAt);
        Assert.True(await restored.ClientPortalCaseGrants.AsNoTracking().AnyAsync(item => item.RevokedAt != null));
        Assert.Contains(
            await restored.ClientPortalAuditEvents.AsNoTracking().Select(item => item.EventType).ToListAsync(),
            action => action == "account_revoked");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, PaymentContext db, RecordingEmailService email, ClientPortalService service, string storageRoot, ICaseDocumentStorage storage)
        {
            _connection = connection;
            Db = db;
            Email = email;
            Service = service;
            StorageRoot = storageRoot;
            Storage = storage;
        }

        public PaymentContext Db { get; }
        public RecordingEmailService Email { get; }
        public ClientPortalService Service { get; }
        public string StorageRoot { get; }
        public ICaseDocumentStorage Storage { get; }
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
            var storageRoot = Path.Combine(Path.GetTempPath(), $"payplanner-client-portal-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:FrontendUrl"] = "https://crm.example.test",
                    [ApplicationFileStoragePaths.RootPathConfigurationKey] = storageRoot,
                })
                .Build();
            var flags = new FixedOptionsMonitor(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions { ClientPortal = enabled },
            });
            var storage = new LocalCaseDocumentStorage(configuration: configuration);
            var service = new ClientPortalService(
                db,
                email,
                configuration,
                flags,
                storage,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storageRoot, "keys"))));
            return new Fixture(connection, db, email, service, storageRoot, storage)
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

        public async Task<PaymentContext> BackupAndRestoreAsync()
        {
            Directory.CreateDirectory(StorageRoot);
            var backupPath = Path.Combine(StorageRoot, "client-portal-backup.sqlite");
            await using (var backup = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
            {
                await backup.OpenAsync();
                ((SqliteConnection)Db.Database.GetDbConnection()).BackupDatabase(backup);
            }

            var restoredConnection = new SqliteConnection("Data Source=:memory:");
            await restoredConnection.OpenAsync();
            await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
            {
                await backup.OpenAsync();
                backup.BackupDatabase(restoredConnection);
            }

            return new PaymentContext(
                new DbContextOptionsBuilder<PaymentContext>()
                    .UseSqlite(restoredConnection, contextOwnsConnection: true)
                    .Options);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true);
        }
    }

    private sealed class FixedOptionsMonitor(AppFeatureFlagsOptions value) : IOptionsMonitor<AppFeatureFlagsOptions>
    {
        public AppFeatureFlagsOptions CurrentValue { get; } = value;
        public AppFeatureFlagsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppFeatureFlagsOptions, string?> listener) => null;
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public string Token { get; private set; } = string.Empty;
        public string Otp { get; private set; } = string.Empty;
        public List<string> Subjects { get; } = [];
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default, EmailSendSettings? settings = null)
        {
            Subjects.Add(subject);
            var tokenMatch = Regex.Match(body, @"#token=([^""&<]+)");
            var otpMatch = Regex.Match(body, @">(\d{6})</strong>");
            if (tokenMatch.Success) Token = Uri.UnescapeDataString(tokenMatch.Groups[1].Value);
            if (otpMatch.Success) Otp = otpMatch.Groups[1].Value;
            return Task.CompletedTask;
        }
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }
}
