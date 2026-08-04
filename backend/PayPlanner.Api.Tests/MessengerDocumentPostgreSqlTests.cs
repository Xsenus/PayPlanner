using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Проверяет перевод атомарных операций публичной доставки в реальный PostgreSQL.</summary>
public sealed class MessengerDocumentPostgreSqlTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    // Имя вместе с GUID должно оставаться короче лимита PostgreSQL в 63 байта.
    private const string DatabasePrefix = "pp_public_doc_test_";

    [Fact]
    public async Task OneTimeDownloadAndCallbacksWorkOnFreshPostgreSqlDatabase()
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
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
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
            var content = "публичный документ PostgreSQL"u8.ToArray();
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
            var prepared = await service.PrepareCaseDocumentAsync(
                user.Id,
                document.Id,
                binding.Id,
                null,
                CancellationToken.None);
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

            Assert.NotNull(await service.ConsumeDownloadAsync(
                prepared.Delivery.PublicId,
                prepared.Token!,
                CancellationToken.None));
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
            await using var terminate = adminConnection.CreateCommand();
            terminate.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
