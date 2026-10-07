using System.Globalization;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public interface IClientPassportEncryptionDualWriteService
{
    bool Enabled { get; }

    Task ProtectAsync(
        ClientPassport passport,
        IReadOnlySet<string>? modifiedPlaintextFields,
        CancellationToken ct);
}

/// <summary>
/// Поддерживает переходный режим read-old/write-new. Plaintext остаётся источником чтения,
/// а изменённые значения дублируются в защищённые колонки только при явном включении.
/// </summary>
public sealed class ClientPassportEncryptionDualWriteService : IClientPassportEncryptionDualWriteService
{
    private readonly IFieldEncryptor _encryptor;
    private readonly bool _enabled;

    public ClientPassportEncryptionDualWriteService(
        IFieldEncryptor encryptor,
        IConfiguration configuration)
    {
        _encryptor = encryptor;
        _enabled = configuration.GetValue("FieldEncryption:Enabled", false)
                   && configuration.GetValue("FieldEncryption:ClientPassportDualWriteEnabled", false);
    }

    public bool Enabled => _enabled;

    public async Task ProtectAsync(
        ClientPassport passport,
        IReadOnlySet<string>? modifiedPlaintextFields,
        CancellationToken ct)
    {
        if (!Enabled)
            return;
        if (passport.Id <= 0)
            throw new InvalidOperationException("Шифрование паспорта требует сохранённый идентификатор записи.");

        await ProtectStringAsync(passport.Id, nameof(ClientPassport.Series), passport.Series,
            modifiedPlaintextFields, passport.SeriesEncrypted,
            value => passport.SeriesEncrypted = value,
            passport.SeriesBlindIndex, value => passport.SeriesBlindIndex = value, ct);
        await ProtectStringAsync(passport.Id, nameof(ClientPassport.Number), passport.Number,
            modifiedPlaintextFields, passport.NumberEncrypted,
            value => passport.NumberEncrypted = value,
            passport.NumberBlindIndex, value => passport.NumberBlindIndex = value, ct);
        await ProtectStringAsync(passport.Id, nameof(ClientPassport.IssuedBy), passport.IssuedBy,
            modifiedPlaintextFields, passport.IssuedByEncrypted,
            value => passport.IssuedByEncrypted = value, null, null, ct);
        await ProtectStringAsync(passport.Id, nameof(ClientPassport.IssuedAt),
            passport.IssuedAt?.ToString("O", CultureInfo.InvariantCulture),
            modifiedPlaintextFields, passport.IssuedAtEncrypted,
            value => passport.IssuedAtEncrypted = value, null, null, ct);
        await ProtectStringAsync(passport.Id, nameof(ClientPassport.DepartmentCode), passport.DepartmentCode,
            modifiedPlaintextFields, passport.DepartmentCodeEncrypted,
            value => passport.DepartmentCodeEncrypted = value, null, null, ct);
        await ProtectStringAsync(passport.Id, nameof(ClientPassport.BirthPlacePassport), passport.BirthPlacePassport,
            modifiedPlaintextFields, passport.BirthPlacePassportEncrypted,
            value => passport.BirthPlacePassportEncrypted = value, null, null, ct);
        await ProtectStringAsync(passport.Id, nameof(ClientPassport.BirthPlaceFias), passport.BirthPlaceFias,
            modifiedPlaintextFields, passport.BirthPlaceFiasEncrypted,
            value => passport.BirthPlaceFiasEncrypted = value, null, null, ct);
    }

    private async Task ProtectStringAsync(
        int passportId,
        string fieldName,
        string? plaintext,
        IReadOnlySet<string>? modifiedPlaintextFields,
        string? currentCiphertext,
        Action<string?> setCiphertext,
        string? currentBlindIndex,
        Action<string?>? setBlindIndex,
        CancellationToken ct)
    {
        var explicitlyChanged = modifiedPlaintextFields is null || modifiedPlaintextFields.Contains(fieldName);
        var needsCiphertext = explicitlyChanged || (plaintext is not null && currentCiphertext is null);
        var needsBlindIndex = setBlindIndex is not null
                              && (explicitlyChanged || (plaintext is not null && currentBlindIndex is null));
        if (!needsCiphertext && !needsBlindIndex)
            return;

        if (plaintext is null)
        {
            if (explicitlyChanged)
            {
                setCiphertext(null);
                setBlindIndex?.Invoke(null);
            }
            return;
        }

        if (needsCiphertext)
        {
            currentCiphertext = await _encryptor.EncryptAsync(
                plaintext,
                $"ClientPassport:{fieldName}:{passportId}",
                ct);
            setCiphertext(currentCiphertext);
        }

        if (needsBlindIndex)
        {
            var blindIndex = await _encryptor.ComputeBlindIndexForCiphertextAsync(
                currentCiphertext ?? throw new InvalidOperationException("Индекс паспорта требует зашифрованную запись."),
                plaintext,
                $"ClientPassport:{fieldName}",
                ct);
            setBlindIndex!(blindIndex);
        }
    }
}
