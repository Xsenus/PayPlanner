using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services;

/// <summary>Compatibility inventory only. Neither key material nor proof of decryption.</summary>
public sealed record DataProtectionRecoveryMetadata(
    int FormatVersion,
    string? ApplicationDiscriminator,
    IReadOnlyList<Guid> KeyIds,
    DateTime CapturedAtUtc)
{
    public bool ContainsKeyMaterial => false;
    public bool DecryptionVerified => false;
}

public sealed class DataProtectionRecoveryInventory(
    IOptions<DataProtectionOptions> options,
    IKeyManager keyManager)
{
    // GetAllKeys reads public identifiers without generating a replacement ring or exporting descriptors.
    public DataProtectionRecoveryMetadata Capture() => new(
        1, options.Value.ApplicationDiscriminator,
        keyManager.GetAllKeys().Select(key => key.KeyId).Distinct().Order().ToArray(), DateTime.UtcNow);

    public static void ValidateMetadata(DataProtectionRecoveryMetadata metadata)
    {
        if (metadata.FormatVersion != 1 || metadata.KeyIds is null
            || metadata.KeyIds.Any(id => id == Guid.Empty)
            || metadata.KeyIds.Distinct().Count() != metadata.KeyIds.Count
            || metadata.CapturedAtUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("Некорректный перечень параметров ключей восстановления.");
    }

    /// <summary>All captured versions must be retained; success still requires actual Unprotect qualification.</summary>
    public static void RequireCompatible(DataProtectionRecoveryMetadata? expected, DataProtectionRecoveryMetadata actual)
    {
        if (expected is null)
            throw new InvalidDataException("Копия не содержит параметров ключей: совместимость восстановления не подтверждена.");
        ValidateMetadata(expected);
        ValidateMetadata(actual);
        if (expected.KeyIds.Count == 0 || actual.KeyIds.Count == 0
            || !string.Equals(expected.ApplicationDiscriminator, actual.ApplicationDiscriminator, StringComparison.Ordinal)
            || !expected.KeyIds.All(actual.KeyIds.ToHashSet().Contains))
            throw new InvalidDataException("Восстановленные ключи отсутствуют, неполны или относятся к другому приложению.");
    }

    // Unlike recovery qualification, an unused application's empty ring is a valid inventory.
    public static DataProtectionRecoveryMetadata CompleteCapture(
        DataProtectionRecoveryMetadata beforeSnapshot, DataProtectionRecoveryMetadata afterSnapshot)
    {
        ValidateMetadata(beforeSnapshot);
        ValidateMetadata(afterSnapshot);
        var available = afterSnapshot.KeyIds.ToHashSet();
        if (!string.Equals(beforeSnapshot.ApplicationDiscriminator, afterSnapshot.ApplicationDiscriminator, StringComparison.Ordinal)
            || !beforeSnapshot.KeyIds.All(available.Contains))
            throw new InvalidDataException("Во время резервирования изменились параметры приложения или были утрачены ключи.");
        return afterSnapshot;
    }
}
