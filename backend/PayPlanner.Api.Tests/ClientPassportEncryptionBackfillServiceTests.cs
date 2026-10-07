using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientPassportEncryptionBackfillServiceTests
{
    [Fact]
    public async Task BackfillPreservesOldCiphertextAndIndexAfterActiveKeyRotation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var passport = await AddPassportAsync(db);
        var keys = new RotatingKeys();
        var encryptor = new AeadFieldEncryptor(keys);
        passport.SeriesEncrypted = await encryptor.EncryptAsync(passport.Series!, $"ClientPassport:Series:{passport.Id}", CancellationToken.None);
        passport.SeriesBlindIndex = await encryptor.ComputeBlindIndexAsync(passport.Series!, "ClientPassport:Series", CancellationToken.None);
        await db.SaveChangesAsync();
        var originalCiphertext = passport.SeriesEncrypted;
        var originalIndex = passport.SeriesBlindIndex;
        keys.Active = "new";
        var service = new ClientPassportEncryptionBackfillService(db, encryptor, Configuration(enabled: true, backfillEnabled: true));
        var result = await service.RunBatchAsync(CancellationToken.None);
        Assert.Equal(1, result.Updated);
        var stored = await db.ClientPassports.AsNoTracking().SingleAsync();
        Assert.Equal(originalCiphertext, stored.SeriesEncrypted);
        Assert.Equal(originalIndex, stored.SeriesBlindIndex);
        Assert.Equal(passport.Series, await encryptor.DecryptAsync(stored.SeriesEncrypted!, $"ClientPassport:Series:{passport.Id}", CancellationToken.None));
    }

    [Fact]
    public async Task DisabledBackfillDoesNotTouchDatabaseOrEncryptor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        var service = new ClientPassportEncryptionBackfillService(
            db,
            new ThrowingEncryptor(),
            Configuration(enabled: false, backfillEnabled: true));

        var result = await service.RunBatchAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal(0, result.Scanned);
    }

    [Fact]
    public async Task EnabledBackfillEncryptsVerifiesAndPersistsCheckpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var passport = await AddPassportAsync(db);
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore());
        var service = new ClientPassportEncryptionBackfillService(
            db,
            encryptor,
            Configuration(enabled: true, backfillEnabled: true));

        var result = await service.RunBatchAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Updated);
        Assert.Equal(passport.Id, result.LastProcessedId);
        var stored = await db.ClientPassports.AsNoTracking().SingleAsync();
        Assert.Equal("4510", stored.Series);
        Assert.Equal("123456", stored.Number);
        Assert.StartsWith("ppenc:v1:", stored.SeriesEncrypted);
        Assert.Equal("4510", await encryptor.DecryptAsync(
            stored.SeriesEncrypted!, $"ClientPassport:Series:{stored.Id}", CancellationToken.None));
        Assert.Equal(await encryptor.ComputeBlindIndexAsync(
            stored.Series!, "ClientPassport:Series", CancellationToken.None), stored.SeriesBlindIndex);
        Assert.Equal(passport.Id.ToString(), await db.SystemSettings
            .Where(item => item.Key == "field-encryption.client-passport.backfill.checkpoint")
            .Select(item => item.Value)
            .SingleAsync());

        var completion = await service.RunBatchAsync(CancellationToken.None);
        Assert.True(completion.CycleCompleted);
        Assert.Equal("0", await db.SystemSettings
            .Where(item => item.Key == "field-encryption.client-passport.backfill.checkpoint")
            .Select(item => item.Value)
            .SingleAsync());
    }

    [Fact]
    public async Task MismatchedCiphertextRollsBackWithoutCheckpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var passport = await AddPassportAsync(db);
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore());
        passport.SeriesEncrypted = await encryptor.EncryptAsync(
            "9999", $"ClientPassport:Series:{passport.Id}", CancellationToken.None);
        await db.SaveChangesAsync();
        var service = new ClientPassportEncryptionBackfillService(
            db,
            encryptor,
            Configuration(enabled: true, backfillEnabled: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBatchAsync(CancellationToken.None));

        Assert.False(await db.SystemSettings.AnyAsync(item =>
            item.Key == "field-encryption.client-passport.backfill.checkpoint"));
        Assert.Null((await db.ClientPassports.AsNoTracking().SingleAsync()).NumberEncrypted);
    }

    private static PaymentContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        return new PaymentContext(options, useObjectAuditModel: false);
    }

    private static async Task<ClientPassport> AddPassportAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Тестовый клиент",
            Email = "passport-backfill@example.test",
            CreatedAt = DateTime.UtcNow,
        };
        var passport = new ClientPassport
        {
            Client = client,
            Series = "4510",
            Number = "123456",
            IssuedBy = "ОВД Тестового района",
            IssuedAt = new DateTime(2020, 1, 2),
            DepartmentCode = "540-001",
            BirthPlacePassport = "г. Новосибирск",
            BirthPlaceFias = "Новосибирск",
            IsPrimary = true,
        };
        db.ClientPassports.Add(passport);
        await db.SaveChangesAsync();
        return passport;
    }

    private static IConfiguration Configuration(bool enabled, bool backfillEnabled)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldEncryption:Enabled"] = enabled.ToString(),
            ["FieldEncryption:ClientPassportBackfillEnabled"] = backfillEnabled.ToString(),
            ["FieldEncryption:ClientPassportBackfillBatchSize"] = "10",
        }).Build();

    private sealed class MemoryKeyStore : IFieldEncryptionKeyStore
    {
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct)
            => GetKeyAsync("test-key", ct);

        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (keyId != "test-key") throw new InvalidOperationException("Ключ не найден.");
            return ValueTask.FromResult(new FieldEncryptionKeyMaterial(
                keyId,
                Enumerable.Repeat((byte)17, 32).ToArray(),
                Enumerable.Repeat((byte)29, 32).ToArray()));
        }
    }

    private sealed class RotatingKeys : IFieldEncryptionKeyStore
    {
        public string Active = "old";
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync(Active, ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var seed = keyId switch { "old" => (byte)17, "new" => (byte)37, _ => throw new InvalidOperationException() };
            return ValueTask.FromResult(new FieldEncryptionKeyMaterial(keyId,
                Enumerable.Repeat(seed, 32).ToArray(), Enumerable.Repeat((byte)(seed + 1), 32).ToArray()));
        }
    }

    private sealed class ThrowingEncryptor : IFieldEncryptor
    {
        public Task<string> EncryptAsync(string plaintext, string authenticatedContext, CancellationToken ct)
            => throw new InvalidOperationException("Encryptor не должен вызываться.");

        public Task<string> DecryptAsync(string ciphertext, string authenticatedContext, CancellationToken ct)
            => throw new InvalidOperationException("Encryptor не должен вызываться.");

        public Task<string> ComputeBlindIndexAsync(string exactValue, string fieldContext, CancellationToken ct)
            => throw new InvalidOperationException("Encryptor не должен вызываться.");
    }
}
