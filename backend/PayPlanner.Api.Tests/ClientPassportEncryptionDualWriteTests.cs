using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientPassportEncryptionDualWriteTests
{
    [Fact]
    public async Task CreateUpdateAndClearKeepPlaintextAndCiphertextInSync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore());
        var dualWrite = new ClientPassportEncryptionDualWriteService(encryptor, EnabledConfiguration());
        await using var db = CreateContext(connection, dualWrite);
        await db.Database.EnsureCreatedAsync();
        var passport = CreatePassport();
        db.ClientPassports.Add(passport);

        await db.SaveChangesAsync();

        Assert.True(passport.Id > 0);
        Assert.Equal("4510", passport.Series);
        Assert.StartsWith("ppenc:v1:", passport.SeriesEncrypted);
        Assert.Equal("4510", await encryptor.DecryptAsync(
            passport.SeriesEncrypted!, $"ClientPassport:Series:{passport.Id}", CancellationToken.None));
        var originalCiphertext = passport.SeriesEncrypted;

        passport.Series = "4511";
        await db.SaveChangesAsync();

        Assert.NotEqual(originalCiphertext, passport.SeriesEncrypted);
        Assert.Equal("4511", await encryptor.DecryptAsync(
            passport.SeriesEncrypted!, $"ClientPassport:Series:{passport.Id}", CancellationToken.None));
        Assert.Equal(await encryptor.ComputeBlindIndexAsync(
            "4511", "ClientPassport:Series", CancellationToken.None), passport.SeriesBlindIndex);

        passport.Number = null;
        await db.SaveChangesAsync();

        Assert.Null(passport.NumberEncrypted);
        Assert.Null(passport.NumberBlindIndex);
        var stored = await db.ClientPassports.AsNoTracking().SingleAsync();
        Assert.Equal("4511", stored.Series);
        Assert.Null(stored.Number);
        Assert.StartsWith("ppenc:v1:", stored.SeriesEncrypted);
    }

    [Fact]
    public async Task EncryptionFailureRollsBackInitialPlaintextInsert()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dualWrite = new ClientPassportEncryptionDualWriteService(
            new ThrowingEncryptor(),
            EnabledConfiguration());
        await using (var db = CreateContext(connection, dualWrite))
        {
            await db.Database.EnsureCreatedAsync();
            db.ClientPassports.Add(CreatePassport());

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var verification = CreateContext(connection, encryption: null);
        Assert.False(await verification.ClientPassports.AnyAsync());
        Assert.False(await verification.Clients.AnyAsync());
    }

    [Fact]
    public async Task DisabledDualWriteDoesNotCallEncryptor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldEncryption:Enabled"] = "true",
            ["FieldEncryption:ClientPassportDualWriteEnabled"] = "false",
        }).Build();
        var dualWrite = new ClientPassportEncryptionDualWriteService(new ThrowingEncryptor(), configuration);
        await using var db = CreateContext(connection, dualWrite);
        await db.Database.EnsureCreatedAsync();
        var passport = CreatePassport();
        db.ClientPassports.Add(passport);

        await db.SaveChangesAsync();

        Assert.Null(passport.SeriesEncrypted);
        Assert.Equal("4510", passport.Series);
    }

    [Fact]
    public async Task SynchronousSaveUsesTheSameAtomicDualWritePath()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore());
        var dualWrite = new ClientPassportEncryptionDualWriteService(encryptor, EnabledConfiguration());
        using var db = CreateContext(connection, dualWrite);
        db.Database.EnsureCreated();
        var passport = CreatePassport();
        db.ClientPassports.Add(passport);

        db.SaveChanges();

        Assert.StartsWith("ppenc:v1:", passport.NumberEncrypted);
        Assert.Equal("123456", await encryptor.DecryptAsync(
            passport.NumberEncrypted!, $"ClientPassport:Number:{passport.Id}", CancellationToken.None));
    }

    [Fact]
    public async Task NewPassportRejectsNonAcceptingSaveBeforeWritingPlaintext()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dualWrite = new ClientPassportEncryptionDualWriteService(
            new AeadFieldEncryptor(new MemoryKeyStore()),
            EnabledConfiguration());
        await using var db = CreateContext(connection, dualWrite);
        await db.Database.EnsureCreatedAsync();
        db.ClientPassports.Add(CreatePassport());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.SaveChangesAsync(acceptAllChangesOnSuccess: false));

        await using var verification = CreateContext(connection, encryption: null);
        Assert.False(await verification.ClientPassports.AnyAsync());
    }

    private static PaymentContext CreateContext(
        SqliteConnection connection,
        IClientPassportEncryptionDualWriteService? encryption)
    {
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        return new PaymentContext(
            options,
            httpContextAccessor: null,
            useObjectAuditModel: false,
            clientPassportEncryption: encryption);
    }

    private static ClientPassport CreatePassport()
        => new()
        {
            Client = new Client
            {
                Name = "Тестовый клиент",
                Email = $"dual-write-{Guid.NewGuid():N}@example.test",
                CreatedAt = DateTime.UtcNow,
            },
            Series = "4510",
            Number = "123456",
            IssuedBy = "ОВД Тестового района",
            IssuedAt = new DateTime(2020, 1, 2),
            DepartmentCode = "540-001",
            BirthPlacePassport = "г. Новосибирск",
            BirthPlaceFias = "Новосибирск",
            IsPrimary = true,
        };

    private static IConfiguration EnabledConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldEncryption:Enabled"] = "true",
            ["FieldEncryption:ClientPassportDualWriteEnabled"] = "true",
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
                Enumerable.Repeat((byte)37, 32).ToArray(),
                Enumerable.Repeat((byte)41, 32).ToArray()));
        }
    }

    private sealed class ThrowingEncryptor : IFieldEncryptor
    {
        public Task<string> EncryptAsync(string plaintext, string authenticatedContext, CancellationToken ct)
            => throw new InvalidOperationException("Тестовый отказ key store.");

        public Task<string> DecryptAsync(string ciphertext, string authenticatedContext, CancellationToken ct)
            => throw new InvalidOperationException("Тестовый отказ key store.");

        public Task<string> ComputeBlindIndexAsync(string exactValue, string fieldContext, CancellationToken ct)
            => throw new InvalidOperationException("Тестовый отказ key store.");
    }
}
