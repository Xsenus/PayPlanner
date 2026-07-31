using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MessengerDocumentDeliveryServiceTests
{
    [Fact]
    public async Task DisabledFeatureRejectsRecipientBinding()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateVerifiedBindingAsync(
                fixture.UserId, fixture.ClientId, null, "telegram", "telegram-bot",
                "Клиент", "123456", "admin-confirmed", CancellationToken.None));

        Assert.Contains("отключена", error.Message);
        Assert.Empty(await fixture.Db.MessengerRecipientBindings.ToListAsync());
    }

    [Fact]
    public async Task PrepareCreatesImmutableSnapshotAndOneTimeDownload()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();

        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, "Документ по делу", CancellationToken.None);

        Assert.NotNull(prepared.Token);
        Assert.Equal(MessengerDocumentDeliveryStatuses.Prepared, prepared.Delivery.Status);
        Assert.Equal(fixture.ExpectedChecksum, prepared.Delivery.ChecksumSha256);
        Assert.NotEqual(fixture.SourcePath, prepared.Delivery.SnapshotPath);

        await File.WriteAllTextAsync(fixture.SourceFullPath, "изменённый исходник");
        var download = await fixture.Service.ConsumeDownloadAsync(
            prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None);
        Assert.NotNull(download);
        Assert.Equal(fixture.ExpectedContent, await File.ReadAllTextAsync(download.FullPath));
        Assert.Null(await fixture.Service.ConsumeDownloadAsync(
            prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareIsIdempotentForSameVersionRecipientAndChecksum()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();

        var first = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        var second = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);

        Assert.Equal(first.Delivery.Id, second.Delivery.Id);
        Assert.NotNull(second.Token);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Single(await fixture.Db.MessengerDocumentDeliveries.ToListAsync());
    }

    [Fact]
    public async Task PrepareRejectsRecipientOfAnotherClient()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var otherClient = new Client { Name = "Другой клиент", IsActive = true };
        fixture.Db.Clients.Add(otherClient);
        await fixture.Db.SaveChangesAsync();
        var binding = await fixture.Service.CreateVerifiedBindingAsync(
            fixture.UserId, otherClient.Id, null, "telegram", "telegram-bot",
            "Другой", "987654", "admin-confirmed", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PrepareCaseDocumentAsync(
                fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));

        Assert.Contains("не принадлежит", error.Message);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.ToListAsync());
    }

    [Fact]
    public async Task OutboxSendsQueuedDeliveryOnceAndClearsProtectedToken()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, "Сообщение", CancellationToken.None);
        await fixture.Service.QueueAsync(prepared.Delivery.Id, CancellationToken.None);
        var provider = new RecordingProvider();
        var processor = fixture.CreateProcessor(provider);

        Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));

        var delivery = await fixture.Db.MessengerDocumentDeliveries.SingleAsync();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Sent, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Null(delivery.ProtectedLinkToken);
        Assert.Single(provider.Requests);
        Assert.Contains("https://crm.example.test/api/public/messenger-document-deliveries/", provider.Requests[0].Link);
        Assert.Contains("token=", provider.Requests[0].Link);
    }

    [Fact]
    public async Task OutboxSchedulesBackoffForTransientFailure()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        await fixture.Service.QueueAsync(prepared.Delivery.Id, CancellationToken.None);
        var processor = fixture.CreateProcessor(new RecordingProvider(transientFailure: true));

        await processor.ProcessBatchAsync(CancellationToken.None);

        var delivery = await fixture.Db.MessengerDocumentDeliveries.SingleAsync();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.True(delivery.NextAttemptAt > DateTime.UtcNow);
        Assert.NotNull(delivery.ProtectedLinkToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _root;
        public PaymentContext Db { get; }
        public MessengerDocumentDeliveryService Service { get; }
        public int UserId { get; }
        public int ClientId { get; }
        public int DocumentId { get; }
        public string SourcePath { get; }
        public string SourceFullPath { get; }
        public string ExpectedContent { get; } = "публичный судебный документ";
        public string ExpectedChecksum { get; }
        public IConfiguration Configuration { get; }
        public IDataProtectionProvider DataProtectionProvider { get; }

        private Fixture(
            SqliteConnection connection,
            string root,
            PaymentContext db,
            MessengerDocumentDeliveryService service,
            int userId,
            int clientId,
            int documentId,
            string sourcePath,
            string sourceFullPath,
            string expectedChecksum,
            IConfiguration configuration,
            IDataProtectionProvider dataProtectionProvider)
        {
            _connection = connection;
            _root = root;
            Db = db;
            Service = service;
            UserId = userId;
            ClientId = clientId;
            DocumentId = documentId;
            SourcePath = sourcePath;
            SourceFullPath = sourceFullPath;
            ExpectedChecksum = expectedChecksum;
            Configuration = configuration;
            DataProtectionProvider = dataProtectionProvider;
        }

        public static async Task<Fixture> CreateAsync(bool enabled)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
            var db = new PaymentContext(options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "admin", Description = "Администратор" };
            var user = new User
            {
                Email = "admin@example.com",
                FullName = "Администратор",
                PasswordHash = "hash",
                Role = role,
                IsActive = true,
                IsApproved = true,
            };
            var client = new Client { Name = "Клиент", IsActive = true };
            var clientCase = new ClientCase { Client = client, Title = "Дело", CaseNumber = "1" };
            db.AddRange(user, clientCase);
            await db.SaveChangesAsync();

            var root = Path.Combine(Path.GetTempPath(), $"payplanner-messenger-delivery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:RootPath"] = root,
                    ["MessengerDocumentDelivery:Enabled"] = enabled.ToString(),
                    ["MessengerDocumentDelivery:LinkLifetimeHours"] = "24",
                    ["MessengerDocumentDelivery:PublicBaseUrl"] = "https://crm.example.test",
                })
                .Build();
            var environment = new TestEnvironment(root);
            var storage = new LocalCaseDocumentStorage(environment, configuration);
            var sourcePath = storage.BuildRelativePath(clientCase.Id, "document.txt");
            var expectedContent = "публичный судебный документ";
            await storage.SaveAsync(sourcePath, System.Text.Encoding.UTF8.GetBytes(expectedContent), CancellationToken.None);
            var document = new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = Guid.NewGuid().ToString("N"),
                Version = 1,
                DocumentType = "court-act",
                Status = "approved",
                OriginalFileName = "document.txt",
                StoredFileName = "document.txt",
                StoragePath = sourcePath,
                ContentType = "text/plain",
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(expectedContent),
                UploadedByUserId = user.Id,
                UploadedByName = user.FullName,
            };
            db.ClientCaseDocuments.Add(document);
            await db.SaveChangesAsync();
            var provider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")));
            var service = new MessengerDocumentDeliveryService(db, storage, provider, environment, configuration);
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedContent)));
            return new Fixture(
                connection, root, db, service, user.Id, client.Id, document.Id,
                sourcePath, storage.ResolvePath(sourcePath), checksum, configuration, provider);
        }

        public Task<MessengerRecipientBinding> CreateBindingAsync()
            => Service.CreateVerifiedBindingAsync(
                UserId, ClientId, null, "telegram", "telegram-bot",
                "Клиент", "123456789", "admin-confirmed", CancellationToken.None);

        public MessengerDocumentOutboxProcessor CreateProcessor(IMessengerDocumentProvider provider)
            => new(
                Db,
                Configuration,
                [provider],
                DataProtectionProvider,
                NullLogger<MessengerDocumentOutboxProcessor>.Instance);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class RecordingProvider(bool transientFailure = false) : IMessengerDocumentProvider
    {
        public string Channel => MessengerDocumentChannels.Telegram;
        public List<MessengerDocumentProviderRequest> Requests { get; } = [];
        public Task<MessengerDocumentProviderResult> SendAsync(MessengerDocumentProviderRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(transientFailure
                ? new MessengerDocumentProviderResult(false, MessengerDocumentDeliveryStatuses.Failed, Error: "temporary", IsTransient: true)
                : new MessengerDocumentProviderResult(true, MessengerDocumentDeliveryStatuses.Sent, "provider-message-1"));
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
