using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PayPlanner.Api.Services;

public sealed record FieldEncryptionKeyMaterial(string KeyId, byte[] MasterKey, byte[] BlindIndexKey);

public interface IFieldEncryptionKeyStore
{
    ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct);
    ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct);
}

public sealed class EnvironmentFieldEncryptionKeyStore : IFieldEncryptionKeyStore
{
    private readonly IConfiguration _configuration;

    public EnvironmentFieldEncryptionKeyStore(IConfiguration configuration) => _configuration = configuration;

    public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct)
    {
        EnsureEnabled();
        var keyId = _configuration["FieldEncryption:ActiveKeyId"]?.Trim();
        if (string.IsNullOrWhiteSpace(keyId))
            throw new InvalidOperationException("Активный ключ прикладного шифрования не настроен.");
        return GetKeyAsync(keyId, ct);
    }

    public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
    {
        EnsureEnabled();
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 64)
            throw new InvalidOperationException("Некорректный идентификатор ключа шифрования.");

        var section = _configuration.GetSection($"FieldEncryption:Keys:{keyId}");
        var masterVariable = section["MasterKeyEnvironmentVariable"];
        var blindVariable = section["BlindIndexKeyEnvironmentVariable"];
        if (string.IsNullOrWhiteSpace(masterVariable) || string.IsNullOrWhiteSpace(blindVariable))
            throw new InvalidOperationException($"Переменные окружения ключа {keyId} не настроены.");

        return ValueTask.FromResult(new FieldEncryptionKeyMaterial(
            keyId,
            ReadKey(masterVariable, "master"),
            ReadKey(blindVariable, "blind index")));
    }

    private void EnsureEnabled()
    {
        if (!_configuration.GetValue("FieldEncryption:Enabled", false))
            throw new InvalidOperationException("Прикладное шифрование безопасно выключено до подключения KMS/Vault.");
    }

    private static byte[] ReadKey(string variableName, string purpose)
    {
        var encoded = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException($"Ключ {purpose} отсутствует в защищённой переменной окружения.");
        try
        {
            var value = Convert.FromBase64String(encoded);
            if (value.Length != 32)
                throw new InvalidOperationException($"Ключ {purpose} должен содержать 32 байта.");
            return value;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Ключ {purpose} имеет некорректный Base64-формат.");
        }
    }
}

public interface IFieldEncryptor
{
    Task<string> EncryptAsync(string plaintext, string authenticatedContext, CancellationToken ct);
    Task<string> DecryptAsync(string ciphertext, string authenticatedContext, CancellationToken ct);
    Task<string> ComputeBlindIndexAsync(string exactValue, string fieldContext, CancellationToken ct);
}

public sealed class AeadFieldEncryptor : IFieldEncryptor
{
    private const string Prefix = "ppenc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly IFieldEncryptionKeyStore _keyStore;

    public AeadFieldEncryptor(IFieldEncryptionKeyStore keyStore) => _keyStore = keyStore;

    public async Task<string> EncryptAsync(string plaintext, string authenticatedContext, CancellationToken ct)
    {
        ValidateContext(authenticatedContext);
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = await _keyStore.GetActiveKeyAsync(ct);
        ValidateKey(key);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var dataNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var wrappedKey = new byte[dataKey.Length];
        var wrapTag = new byte[TagSize];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = new byte[plaintextBytes.Length];
        var dataTag = new byte[TagSize];
        try
        {
            using (var master = new AesGcm(key.MasterKey, TagSize))
                master.Encrypt(wrapNonce, dataKey, wrappedKey, wrapTag, Encoding.UTF8.GetBytes($"PayPlanner.FieldDataKey.v1|{key.KeyId}"));
            using (var data = new AesGcm(dataKey, TagSize))
                data.Encrypt(dataNonce, plaintextBytes, encrypted, dataTag, Encoding.UTF8.GetBytes(authenticatedContext));

            var payload = new CipherPayload(
                key.KeyId,
                Convert.ToBase64String(wrapNonce),
                Convert.ToBase64String(wrappedKey),
                Convert.ToBase64String(wrapTag),
                Convert.ToBase64String(dataNonce),
                Convert.ToBase64String(encrypted),
                Convert.ToBase64String(dataTag));
            return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(key.MasterKey);
            CryptographicOperations.ZeroMemory(key.BlindIndexKey);
        }
    }

    public async Task<string> DecryptAsync(string ciphertext, string authenticatedContext, CancellationToken ct)
    {
        ValidateContext(authenticatedContext);
        if (string.IsNullOrWhiteSpace(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Неизвестный или устаревший формат зашифрованного поля.");
        CipherPayload payload;
        try
        {
            var json = Convert.FromBase64String(ciphertext[Prefix.Length..]);
            payload = JsonSerializer.Deserialize<CipherPayload>(json)
                ?? throw new InvalidOperationException("Пустой контейнер зашифрованного поля.");
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new InvalidOperationException("Повреждён контейнер зашифрованного поля.", error);
        }

        var key = await _keyStore.GetKeyAsync(payload.KeyId, ct);
        ValidateKey(key);
        var dataKey = new byte[32];
        byte[] plaintext = [];
        try
        {
            using (var master = new AesGcm(key.MasterKey, TagSize))
                master.Decrypt(
                    Decode(payload.WrapNonce, NonceSize),
                    Decode(payload.WrappedKey, 32),
                    Decode(payload.WrapTag, TagSize),
                    dataKey,
                    Encoding.UTF8.GetBytes($"PayPlanner.FieldDataKey.v1|{payload.KeyId}"));

            var encrypted = Decode(payload.Data, null);
            plaintext = new byte[encrypted.Length];
            using (var data = new AesGcm(dataKey, TagSize))
                data.Decrypt(
                    Decode(payload.DataNonce, NonceSize),
                    encrypted,
                    Decode(payload.DataTag, TagSize),
                    plaintext,
                    Encoding.UTF8.GetBytes(authenticatedContext));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException error)
        {
            throw new InvalidOperationException("Целостность зашифрованного поля или контекст записи не подтверждены.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key.MasterKey);
            CryptographicOperations.ZeroMemory(key.BlindIndexKey);
        }
    }

    public async Task<string> ComputeBlindIndexAsync(string exactValue, string fieldContext, CancellationToken ct)
    {
        ValidateContext(fieldContext);
        ArgumentNullException.ThrowIfNull(exactValue);
        var key = await _keyStore.GetActiveKeyAsync(ct);
        ValidateKey(key);
        var normalized = exactValue.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var input = Encoding.UTF8.GetBytes($"{fieldContext}\0{normalized}");
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key.BlindIndexKey, input)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(key.MasterKey);
            CryptographicOperations.ZeroMemory(key.BlindIndexKey);
        }
    }

    private static byte[] Decode(string value, int? expectedLength)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (expectedLength.HasValue && bytes.Length != expectedLength.Value)
                throw new InvalidOperationException("Некорректная длина криптографического параметра.");
            return bytes;
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException("Повреждён криптографический параметр.", error);
        }
    }

    private static void ValidateContext(string context)
    {
        if (string.IsNullOrWhiteSpace(context) || context.Length > 512)
            throw new InvalidOperationException("Для шифрования обязателен контекст entity/field/id.");
    }

    private static void ValidateKey(FieldEncryptionKeyMaterial key)
    {
        if (string.IsNullOrWhiteSpace(key.KeyId) || key.MasterKey.Length != 32 || key.BlindIndexKey.Length != 32)
            throw new InvalidOperationException("Хранилище вернуло некорректный ключ шифрования.");
    }

    private sealed record CipherPayload(
        string KeyId,
        string WrapNonce,
        string WrappedKey,
        string WrapTag,
        string DataNonce,
        string Data,
        string DataTag);
}
