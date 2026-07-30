using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtSyncDatabaseIntegrationTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_court_sync_test_";

    [Fact]
    public async Task Sqlite_CourtSyncMigrationsAndConstraints_WorkOnFreshDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "payplanner-court-sync-tests");
        var databasePath = Path.Combine(directory, $"court-sync-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(directory);

        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .UsePayPlannerSqliteMigrations()
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using var context = new PaymentContext(options);
            await context.Database.MigrateAsync();

            var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20260729120729_AddCourtEventExternalKeys", appliedMigrations);
            Assert.Contains("20260729133312_AddCourtSyncProvenance", appliedMigrations);

            await AssertCourtSyncRoundTripAndUniqueConstraintAsync(context);
            await AssertSmsDiagnosticsQueryAsync(context);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-shm");
            DeleteIfExists(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task PostgreSql_CourtSyncMigrationsAndConstraints_WorkOnFreshDatabase()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = PostgreSqlDatabasePrefix + Guid.NewGuid().ToString("N");
        AssertSafeTemporaryDatabaseName(databaseName);

        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };

        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var createCommand = adminConnection.CreateCommand())
            {
                createCommand.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await createCommand.ExecuteNonQueryAsync();
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

            await using var context = new PostgreSqlPaymentContext(options);
            await context.Database.MigrateAsync();

            var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20260729120612_AddCourtEventExternalKeys", appliedMigrations);
            Assert.Contains("20260729133331_AddCourtSyncProvenance", appliedMigrations);

            await AssertCourtSyncRoundTripAndUniqueConstraintAsync(context);
            await AssertSmsDiagnosticsQueryAsync(context);
        }
        finally
        {
            await using var terminateCommand = adminConnection.CreateCommand();
            terminateCommand.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName AND pid <> pg_backend_pid()
                """;
            terminateCommand.Parameters.AddWithValue("databaseName", databaseName);
            await terminateCommand.ExecuteNonQueryAsync();

            AssertSafeTemporaryDatabaseName(databaseName);
            await using var dropCommand = adminConnection.CreateCommand();
            dropCommand.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            await dropCommand.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task AssertCourtSyncRoundTripAndUniqueConstraintAsync(PaymentContext context)
    {
        var checkedAt = new DateTime(2026, 7, 30, 8, 15, 0, DateTimeKind.Utc);
        var client = new Client
        {
            Name = "Интеграционная проверка суда",
            IsActive = true,
            CreatedAt = checkedAt,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Проверка судебной синхронизации",
            Status = ClientCaseStatus.Open,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "court-sync-document",
            DocumentType = "court-ruling",
            Status = "available",
            OriginalFileName = "ruling.pdf",
            StoredFileName = "ruling.pdf",
            StoragePath = "court-sync-tests/ruling.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            SourceType = "sudrf",
            SourceUrl = "https://example.sudrf.ru/case/42",
            SourceCheckedAt = checkedAt,
            SourceConfidence = "official-source",
            UploadedByName = "Судебная синхронизация",
            UploadedAt = checkedAt,
        };
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCase = clientCase,
            DeadlineType = "appeal",
            Title = "Срок обжалования",
            DueAt = checkedAt.AddDays(30),
            SourceType = "sudrf",
            SourceUrl = "https://example.sudrf.ru/case/42",
            SourceCheckedAt = checkedAt,
            SourceConfidence = "official-source",
            CreatedAt = checkedAt,
        };
        var courtEvent = new ClientCaseCourtEvent
        {
            ClientCase = clientCase,
            EventType = "hearing",
            Title = "Судебное заседание",
            ScheduledAt = checkedAt.AddDays(7),
            CourtInstance = "first",
            CourtName = "Тестовый районный суд",
            MovementStatus = "scheduled",
            SourceType = "sudrf",
            SourceUrl = "https://example.sudrf.ru/case/42",
            SourceCheckedAt = checkedAt,
            SourceConfidence = "official-source",
            ExternalKey = "sudrf-event-42",
            ClientCaseDocument = document,
            ProceduralDeadline = deadline,
            CreatedAt = checkedAt,
        };
        var party = new ClientCaseCourtParty
        {
            ClientCase = clientCase,
            DisplayName = "Тестовый участник",
            Side = "claimant",
            ProcessRole = "Истец",
            SourceType = "sudrf",
            SourceUrl = "https://example.sudrf.ru/case/42",
            SourceCheckedAt = checkedAt,
            SourceConfidence = "official-source",
            CreatedAt = checkedAt,
        };

        context.AddRange(courtEvent, party);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var storedEvent = await context.ClientCaseCourtEvents
            .AsNoTracking()
            .SingleAsync(item => item.ExternalKey == "sudrf-event-42");
        var storedParty = await context.ClientCaseCourtParties.AsNoTracking().SingleAsync();
        var storedDocument = await context.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var storedDeadline = await context.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();

        Assert.Equal("sudrf", storedEvent.SourceType);
        Assert.Equal(checkedAt, storedEvent.SourceCheckedAt);
        Assert.Equal("official-source", storedEvent.SourceConfidence);
        Assert.Equal("official-source", storedParty.SourceConfidence);
        Assert.Equal(checkedAt, storedDocument.SourceCheckedAt);
        Assert.Equal("https://example.sudrf.ru/case/42", storedDeadline.SourceUrl);

        context.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = storedEvent.ClientCaseId,
            EventType = "hearing",
            Title = "Повтор того же события",
            ScheduledAt = checkedAt.AddDays(8),
            CourtInstance = "first",
            MovementStatus = "scheduled",
            SourceType = storedEvent.SourceType,
            ExternalKey = storedEvent.ExternalKey,
            CreatedAt = checkedAt,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
    }

    private static async Task AssertSmsDiagnosticsQueryAsync(PaymentContext context)
    {
        var now = DateTime.UtcNow;
        context.NotificationDeliveries.AddRange(
            new NotificationDelivery
            {
                EventKey = "sms:database:sent",
                Channel = NotificationChannels.Sms,
                SenderName = "SMS",
                RecipientAddress = "+79990000101",
                Subject = "SMS",
                BodyPreview = "Доставлено",
                Status = NotificationDeliveryStatus.Sent,
                CreatedAt = now.AddHours(-2),
                SentAt = now.AddHours(-2).AddMinutes(1),
            },
            new NotificationDelivery
            {
                EventKey = "sms:database:failed",
                Channel = NotificationChannels.Sms,
                SenderName = "SMS",
                RecipientAddress = "+79990000102",
                Subject = "SMS",
                BodyPreview = "Ошибка",
                Status = NotificationDeliveryStatus.Failed,
                Error = "Безопасная тестовая ошибка.",
                CreatedAt = now.AddHours(-1),
            });
        await context.SaveChangesAsync();
        var service = new SystemSmsNotificationService(context);
        await service.SaveDiagnosticsAsync(new SmsProviderDiagnosticsSnapshot(
            now,
            true,
            900m,
            "RUB",
            null,
            "SMS-канал доступен."));

        var settings = await service.GetSettingsAsync();

        Assert.True(settings.Diagnostics.ProviderHealthy);
        Assert.Equal(900m, settings.Diagnostics.Balance);
        Assert.Equal(1, settings.Diagnostics.DeliveredCount);
        Assert.Equal(1, settings.Diagnostics.FailedCount);
        Assert.Equal(50m, settings.Diagnostics.DeliveredRatePercent);
    }

    private static void AssertSafeTemporaryDatabaseName(string databaseName)
    {
        Assert.StartsWith(PostgreSqlDatabasePrefix, databaseName, StringComparison.Ordinal);
        var suffix = databaseName[PostgreSqlDatabasePrefix.Length..];
        Assert.Equal(32, suffix.Length);
        Assert.All(suffix, character => Assert.True(Uri.IsHexDigit(character)));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
