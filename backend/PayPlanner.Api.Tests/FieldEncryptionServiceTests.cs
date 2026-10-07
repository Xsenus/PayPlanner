using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class FieldEncryptionServiceTests
{
    [Fact]
    public async Task EncryptRoundTripUsesRandomNonceAndAuthenticatedContext()
    {
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore("key-1"));
        var first = await encryptor.EncryptAsync("4510 123456", "ClientPassport:Number:42", CancellationToken.None);
        var second = await encryptor.EncryptAsync("4510 123456", "ClientPassport:Number:42", CancellationToken.None);

        Assert.StartsWith("ppenc:v1:", first);
        Assert.NotEqual(first, second);
        Assert.Equal("4510 123456", await encryptor.DecryptAsync(first, "ClientPassport:Number:42", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encryptor.DecryptAsync(first, "ClientPassport:Number:43", CancellationToken.None));
    }

    [Fact]
    public async Task TamperedCiphertextAndWrongKeyAreRejected()
    {
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore("key-1"));
        var encrypted = await encryptor.EncryptAsync("секрет", "Client:Notes:10", CancellationToken.None);
        var payload = Convert.FromBase64String(encrypted["ppenc:v1:".Length..]);
        payload[^5] ^= 0x20;
        var tampered = "ppenc:v1:" + Convert.ToBase64String(payload);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encryptor.DecryptAsync(tampered, "Client:Notes:10", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AeadFieldEncryptor(new MemoryKeyStore("key-1", seed: 99))
                .DecryptAsync(encrypted, "Client:Notes:10", CancellationToken.None));
    }

    [Fact]
    public async Task RotationReadsOldKeyAndWritesActiveKey()
    {
        var store = new RotatingMemoryKeyStore();
        var encryptor = new AeadFieldEncryptor(store);
        var oldCiphertext = await encryptor.EncryptAsync("старое", "Client:Address:7", CancellationToken.None);
        store.ActiveKeyId = "key-2";
        var newCiphertext = await encryptor.EncryptAsync("новое", "Client:Address:7", CancellationToken.None);

        Assert.Equal("старое", await encryptor.DecryptAsync(oldCiphertext, "Client:Address:7", CancellationToken.None));
        Assert.Equal("новое", await encryptor.DecryptAsync(newCiphertext, "Client:Address:7", CancellationToken.None));
        Assert.NotEqual(oldCiphertext, newCiphertext);
    }

    [Fact]
    public async Task BlindIndexIsDeterministicExactAndFieldScoped()
    {
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore("key-1"));
        var first = await encryptor.ComputeBlindIndexAsync(" Client@Example.RU ", "Client:Email", CancellationToken.None);
        var normalized = await encryptor.ComputeBlindIndexAsync("client@example.ru", "Client:Email", CancellationToken.None);
        var anotherField = await encryptor.ComputeBlindIndexAsync("client@example.ru", "User:Email", CancellationToken.None);

        Assert.Equal(first, normalized);
        Assert.NotEqual(first, anotherField);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public async Task HistoricalIndexUsesCiphertextKeyAfterRotation()
    {
        var store = new RotatingMemoryKeyStore();
        var encryptor = new AeadFieldEncryptor(store);
        var ciphertext = await encryptor.EncryptAsync(" 4510 ", "ClientPassport:Series:42", CancellationToken.None);
        var oldIndex = await encryptor.ComputeBlindIndexAsync(" 4510 ", "ClientPassport:Series", CancellationToken.None);
        store.ActiveKeyId = "key-2";
        var newIndex = await encryptor.ComputeBlindIndexAsync("4510", "ClientPassport:Series", CancellationToken.None);
        Assert.NotEqual(oldIndex, newIndex);
        Assert.Equal(oldIndex, await encryptor.ComputeBlindIndexForCiphertextAsync(ciphertext, "4510", "ClientPassport:Series", CancellationToken.None));
    }

    [Fact]
    public async Task MissingIndexRepairKeepsHistoricalCiphertextAndKey()
    {
        var store = new RotatingMemoryKeyStore();
        var encryptor = new AeadFieldEncryptor(store);
        var passport = new PayPlanner.Api.Models.ClientPassport { Id = 42, Series = "4510" };
        passport.SeriesEncrypted = await encryptor.EncryptAsync("4510", "ClientPassport:Series:42", CancellationToken.None);
        var original = passport.SeriesEncrypted;
        var oldIndex = await encryptor.ComputeBlindIndexAsync("4510", "ClientPassport:Series", CancellationToken.None);
        store.ActiveKeyId = "key-2";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldEncryption:Enabled"] = "true",
            ["FieldEncryption:ClientPassportDualWriteEnabled"] = "true",
        }).Build();
        var writer = new ClientPassportEncryptionDualWriteService(encryptor, configuration);
        await writer.ProtectAsync(passport, new HashSet<string>(), CancellationToken.None);
        Assert.Equal(original, passport.SeriesEncrypted);
        Assert.Equal(oldIndex, passport.SeriesBlindIndex);
    }

    [Fact]
    public async Task EnvironmentKeyStoreIsDisabledByDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldEncryption:Enabled"] = "false"
        }).Build();
        var store = new EnvironmentFieldEncryptionKeyStore(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetActiveKeyAsync(CancellationToken.None));
        Assert.Contains("выключено", error.Message);
    }

    [Fact]
    public async Task LegacyPlaintextIsNeverSilentlyAccepted()
    {
        var encryptor = new AeadFieldEncryptor(new MemoryKeyStore("key-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encryptor.DecryptAsync("обычный текст", "Client:Notes:1", CancellationToken.None));
    }

    private sealed class MemoryKeyStore(string keyId, byte seed = 1) : IFieldEncryptionKeyStore
    {
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync(keyId, ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string requestedKeyId, CancellationToken ct)
        {
            if (requestedKeyId != keyId) throw new InvalidOperationException("Ключ не найден.");
            return ValueTask.FromResult(Material(requestedKeyId, seed));
        }
    }

    private sealed class RotatingMemoryKeyStore : IFieldEncryptionKeyStore
    {
        public string ActiveKeyId { get; set; } = "key-1";
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync(ActiveKeyId, ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct) =>
            ValueTask.FromResult(keyId switch
            {
                "key-1" => Material(keyId, 11),
                "key-2" => Material(keyId, 22),
                _ => throw new InvalidOperationException("Ключ не найден.")
            });
    }

    private static FieldEncryptionKeyMaterial Material(string keyId, byte seed) => new(
        keyId,
        Enumerable.Repeat(seed, 32).ToArray(),
        Enumerable.Repeat((byte)(seed + 1), 32).ToArray());
}
