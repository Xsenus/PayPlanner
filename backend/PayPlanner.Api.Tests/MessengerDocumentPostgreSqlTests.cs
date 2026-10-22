using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Проверяет перевод атомарных операций публичной доставки в реальный PostgreSQL.</summary>
public sealed class MessengerDocumentPostgreSqlTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    // Имя вместе с GUID должно оставаться короче лимита PostgreSQL в 63 байта.
    private const string DatabasePrefix = "pp_public_doc_test_";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task OneTimeDownloadAndCallbacksWorkOnFreshPostgreSqlDatabase(bool managedRead, bool managedWrite)
        => VerifyOneTimeDownloadAsync(managedRead, managedWrite, false);

    [Fact]
    public Task GeneratedSnapshotUsesAdmissionAndOneTimeDownloadOnPostgres()
        => VerifyOneTimeDownloadAsync(true, true, true);

    private static async Task VerifyOneTimeDownloadAsync(bool managedRead, bool managedWrite, bool generated)
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");
        Assert.StartsWith(DatabasePrefix, databaseName, StringComparison.Ordinal);
        var root = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(root);
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        Assert.True(adminBuilder.Host is "127.0.0.1" or "::1" or "localhost", "Disposable PostgreSQL tests require literal loopback.");
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();
        var created = false;
        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
                created = true;
                Console.WriteLine($"Owned messenger snapshot regression database: {databaseName}");
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
            await db.Database.MigrateAsync();

            var role = new Role { Name = "admin", Description = "Администратор" };
            var user = new User
            {
                Email = "public-document@example.test",
                FullName = "Тест публичного документа",
                PasswordHash = "hash",
                Role = role,
                IsActive = true,
                IsApproved = true,
            };
            var client = new Client { Name = "Клиент PostgreSQL", IsActive = true };
            var clientCase = new ClientCase { Client = client, Title = "Дело PostgreSQL", CaseNumber = "PG-1" };
            db.AddRange(user, clientCase);
            await db.SaveChangesAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:RootPath"] = root,
                    ["MessengerDocumentDelivery:Enabled"] = "true",
                    ["MessengerDocumentDelivery:LinkLifetimeHours"] = "24",
                    ["MessengerDocumentDelivery:Providers:Telegram:WebhookSecret"] = "callback-secret",
                })
                .Build();
            var environment = new TestEnvironment(root);
            var storage = new LocalCaseDocumentStorage(environment, configuration);
            var content = generated ? "%PDF-1.7 generated snapshot PostgreSQL"u8.ToArray() : "публичный документ PostgreSQL"u8.ToArray();
            var sourcePath = storage.BuildRelativePath(clientCase.Id, "document.txt");
            await storage.SaveAsync(sourcePath, content, CancellationToken.None);
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
                SizeBytes = content.LongLength,
                UploadedByUserId = user.Id,
                UploadedByName = user.FullName,
            };
            db.ClientCaseDocuments.Add(document);
            await db.SaveChangesAsync();

            var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var service = new MessengerDocumentDeliveryService(db, storage, dataProtectionProvider, environment, configuration);
            if (managedWrite)
            {
                var registry = new StorageProviderRegistry([new LocalStorageProvider(environment, configuration), new LegacyLocalStorageProvider(environment, configuration)]);
                var storageMonitor = new StaticStorageOptions(new()
                {
                    Enabled = true,
                    UploadCapacityChecksEnabled = true,
                    ProductReadEnabledClasses = ["messenger-delivery"],
                    ProductWriteEnabledClasses = ["messenger-delivery"]
                });
                var policy = new StoragePolicyService(storageMonitor, registry);
                var objects = new StorageObjectService(db, policy, registry, storageMonitor);
                service = new MessengerDocumentDeliveryService(db, storage, dataProtectionProvider, environment, configuration,
                    new ProductStorageReader(db, objects, storageMonitor), managedStorage: objects,
                    storagePublisher: new ProductStorageBindingService(db, policy), storageOptions: storageMonitor, stageCapacity: new StorageStageCapacityService(db, registry, storageMonitor, environment, configuration));
            }
            var binding = await service.CreateVerifiedBindingAsync(
                user.Id,
                client.Id,
                null,
                MessengerDocumentChannels.Telegram,
                "telegram-bot",
                "Получатель",
                "123456",
                "test-confirmation",
                CancellationToken.None);
            Task<PreparedMessengerDocumentDelivery> PrepareSnapshotAsync()
                => generated
                    ? service.PrepareGeneratedDocumentAsync(user.Id, "invoice", 77, 1, client.Id, "invoice.pdf", "application/pdf", new MemoryStream(content), content.Length, binding.Id, null, CancellationToken.None)
                    : service.PrepareCaseDocumentAsync(user.Id, document.Id, binding.Id, null, CancellationToken.None);
            var prepared = await PrepareSnapshotAsync();
            if (managedWrite)
            {
                Assert.Equal(prepared.Delivery.Id, (await db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
                Assert.Equal(prepared.Delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (await db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
                var replay = await PrepareSnapshotAsync();
                Assert.Equal(prepared.Delivery.Id, replay.Delivery.Id);
                prepared = replay;
                File.Delete(Path.Combine(root, "messenger-document-deliveries", prepared.Delivery.SnapshotPath));
            }
            else if (managedRead)
            {
                var legacy = new LegacyLocalStorageProvider(environment, configuration);
                var backfill = new LegacyStorageCatalogBackfillService(db, legacy, environment, configuration);
                var report = await backfill.RunAsync(new(LegacyStorageSources.MessengerSnapshots, Apply: true), CancellationToken.None);
                Assert.Equal(1, report.Registered);
                var registry = new StorageProviderRegistry([legacy]);
                var storageMonitor = new StaticStorageOptions(new()
                {
                    Enabled = true,
                    ProductReadEnabledClasses = ["messenger-delivery"],
                    DefaultPolicy = new() { WriteProviders = ["legacy-local"], ReadProviders = ["legacy-local"] },
                });
                var objects = new StorageObjectService(db, new StoragePolicyService(storageMonitor, registry), registry, storageMonitor);
                service = new MessengerDocumentDeliveryService(db, storage, dataProtectionProvider, environment, configuration,
                    new ProductStorageReader(db, objects, storageMonitor));
            }
            prepared.Delivery.Status = MessengerDocumentDeliveryStatuses.Sent;
            prepared.Delivery.ProviderMessageId = "pg-message-1";
            await db.SaveChangesAsync();

            var controller = new PublicMessengerDocumentDeliveriesController(service, db, configuration)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            controller.Request.Headers["X-Document-Delivery-Webhook-Secret"] = "callback-secret";
            Assert.IsType<OkObjectResult>(await controller.Callback(
                MessengerDocumentChannels.Telegram,
                new MessengerDocumentDeliveryCallbackRequest { ProviderMessageId = "pg-message-1", Status = "delivered" },
                CancellationToken.None));
            Assert.IsType<OkObjectResult>(await controller.Callback(
                MessengerDocumentChannels.Telegram,
                new MessengerDocumentDeliveryCallbackRequest { ProviderMessageId = "pg-message-1", Status = "read" },
                CancellationToken.None));
            Assert.IsType<OkObjectResult>(await controller.Callback(
                MessengerDocumentChannels.Telegram,
                new MessengerDocumentDeliveryCallbackRequest
                {
                    ProviderMessageId = "pg-message-1",
                    Status = "failed",
                    Error = "запоздалая ошибка",
                },
                CancellationToken.None));

            await using var downloaded = await service.ConsumeDownloadAsync(
                prepared.Delivery.PublicId,
                prepared.Token!,
                CancellationToken.None);
            Assert.NotNull(downloaded);
            if (managedRead)
            {
                Assert.NotNull(downloaded.Content);
                using var memory = new MemoryStream();
                await downloaded.Content.CopyToAsync(memory);
                Assert.Equal(content, memory.ToArray());
            }
            else Assert.Null(downloaded.Content);
            Assert.Null(await service.ConsumeDownloadAsync(
                prepared.Delivery.PublicId,
                prepared.Token!,
                CancellationToken.None));
            db.ChangeTracker.Clear();
            var stored = await db.MessengerDocumentDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(MessengerDocumentDeliveryStatuses.Read, stored.Status);
            Assert.Equal(1, stored.DownloadCount);
            Assert.NotNull(stored.DeliveredAt);
            Assert.NotNull(stored.ReadAt);
        }
        finally
        {
            if (created)
            {
                Assert.True(databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(databaseName[DatabasePrefix.Length..], "N", out _));
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StaticStorageOptions(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
