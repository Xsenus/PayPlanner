using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientPassportEncryptionMigrationTests
{
    private const string PreviousMigration = "20260731200157_AddSmsProviderDeliveryStatus";
    private const string EncryptionMigration = "20260731201628_AddClientPassportEncryptionColumns";

    [Fact]
    public async Task Migration_AddsNullableEncryptionColumnsWithoutChangingPlaintext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "payplanner-passport-encryption-migrations");
        var databasePath = Path.Combine(directory, $"passport-encryption-{Guid.NewGuid():N}.db");
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

            int clientId;
            await using (var legacyContext = new PaymentContext(options))
            {
                await legacyContext.Database.MigrateAsync(PreviousMigration);
                var client = new Client
                {
                    Name = "Тест шифрования паспорта",
                    Email = "passport-encryption@example.test",
                    Phone = "+79990000000",
                    CreatedAt = DateTime.UtcNow,
                };
                legacyContext.Clients.Add(client);
                await legacyContext.SaveChangesAsync();
                clientId = client.Id;
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO ClientPassports
                        (ClientId, Series, Number, IssuedBy, IssuedAt, DepartmentCode,
                         BirthPlacePassport, BirthPlaceFias, ValidFrom, ValidTo, IsPrimary, CreatedAt)
                    VALUES
                        ($clientId, '4510', '123456', 'ОВД Тестового района', '2020-01-02 00:00:00', '540-001',
                         'г. Новосибирск', 'Новосибирск', NULL, NULL, 1, '2026-08-01 00:00:00');
                    """;
                insert.Parameters.AddWithValue("$clientId", clientId);
                await insert.ExecuteNonQueryAsync();
            }

            await using (var upgradedContext = new PaymentContext(options))
            {
                await upgradedContext.Database.MigrateAsync();
                var appliedMigrations = await upgradedContext.Database.GetAppliedMigrationsAsync();
                Assert.Contains(EncryptionMigration, appliedMigrations);

                var passport = await upgradedContext.ClientPassports.AsNoTracking().SingleAsync();
                Assert.Equal("4510", passport.Series);
                Assert.Equal("123456", passport.Number);
                Assert.Equal("ОВД Тестового района", passport.IssuedBy);
                Assert.Equal("540-001", passport.DepartmentCode);
                Assert.Equal("г. Новосибирск", passport.BirthPlacePassport);
                Assert.Equal("Новосибирск", passport.BirthPlaceFias);
                Assert.Null(passport.SeriesEncrypted);
                Assert.Null(passport.SeriesBlindIndex);
                Assert.Null(passport.NumberEncrypted);
                Assert.Null(passport.NumberBlindIndex);
                Assert.Null(passport.IssuedByEncrypted);
                Assert.Null(passport.IssuedAtEncrypted);
                Assert.Null(passport.DepartmentCodeEncrypted);
                Assert.Null(passport.BirthPlacePassportEncrypted);
                Assert.Null(passport.BirthPlaceFiasEncrypted);
            }

            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_index_list('ClientPassports')
                WHERE name = 'IX_ClientPassports_ClientId_SeriesBlind_NumberBlind';
                """;
            Assert.Equal(1L, (long)(await indexCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-shm");
            DeleteIfExists(databasePath + "-wal");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
