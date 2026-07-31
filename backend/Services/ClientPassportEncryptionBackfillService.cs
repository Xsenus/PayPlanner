using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed record ClientPassportEncryptionBackfillResult(
    bool Enabled,
    int Scanned,
    int Updated,
    int LastProcessedId,
    bool CycleCompleted);

public interface IClientPassportEncryptionBackfillService
{
    Task<ClientPassportEncryptionBackfillResult> RunBatchAsync(CancellationToken ct);
}

/// <summary>
/// Выполняет ограниченный и обратимый перенос паспортных полей в зашифрованные колонки.
/// Сервис не запускается в фоне и ничего не делает без двух явно включённых флагов.
/// </summary>
public sealed class ClientPassportEncryptionBackfillService : IClientPassportEncryptionBackfillService
{
    private const string CheckpointKey = "field-encryption.client-passport.backfill.checkpoint";
    private const string MetricsKey = "field-encryption.client-passport.backfill.metrics";
    private readonly PaymentContext _db;
    private readonly IFieldEncryptor _encryptor;
    private readonly IConfiguration _configuration;

    public ClientPassportEncryptionBackfillService(
        PaymentContext db,
        IFieldEncryptor encryptor,
        IConfiguration configuration)
    {
        _db = db;
        _encryptor = encryptor;
        _configuration = configuration;
    }

    public async Task<ClientPassportEncryptionBackfillResult> RunBatchAsync(CancellationToken ct)
    {
        // Проверяем оба флага до первого обращения к БД и key store.
        if (!_configuration.GetValue("FieldEncryption:Enabled", false)
            || !_configuration.GetValue("FieldEncryption:ClientPassportBackfillEnabled", false))
        {
            return new ClientPassportEncryptionBackfillResult(false, 0, 0, 0, false);
        }

        var batchSize = Math.Clamp(
            _configuration.GetValue("FieldEncryption:ClientPassportBackfillBatchSize", 50),
            1,
            200);
        var checkpoint = await ReadCheckpointAsync(ct);
        var passports = await _db.ClientPassports
            .Where(item => item.Id > checkpoint)
            .OrderBy(item => item.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (passports.Count == 0)
        {
            await using var completionTransaction = await _db.Database.BeginTransactionAsync(ct);
            await WriteStateAsync(0, new BackfillMetrics(0, 0, 0, true, DateTime.UtcNow), ct);
            await _db.SaveChangesAsync(ct);
            await completionTransaction.CommitAsync(ct);
            return new ClientPassportEncryptionBackfillResult(true, 0, 0, 0, true);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var updated = 0;
        foreach (var passport in passports)
        {
            ct.ThrowIfCancellationRequested();
            var changed = false;
            changed |= await ProtectStringAsync(passport.Id, "Series", passport.Series,
                () => passport.SeriesEncrypted, value => passport.SeriesEncrypted = value,
                () => passport.SeriesBlindIndex, value => passport.SeriesBlindIndex = value, ct);
            changed |= await ProtectStringAsync(passport.Id, "Number", passport.Number,
                () => passport.NumberEncrypted, value => passport.NumberEncrypted = value,
                () => passport.NumberBlindIndex, value => passport.NumberBlindIndex = value, ct);
            changed |= await ProtectStringAsync(passport.Id, "IssuedBy", passport.IssuedBy,
                () => passport.IssuedByEncrypted, value => passport.IssuedByEncrypted = value, null, null, ct);
            changed |= await ProtectStringAsync(passport.Id, "IssuedAt",
                passport.IssuedAt?.ToString("O", CultureInfo.InvariantCulture),
                () => passport.IssuedAtEncrypted, value => passport.IssuedAtEncrypted = value, null, null, ct);
            changed |= await ProtectStringAsync(passport.Id, "DepartmentCode", passport.DepartmentCode,
                () => passport.DepartmentCodeEncrypted, value => passport.DepartmentCodeEncrypted = value, null, null, ct);
            changed |= await ProtectStringAsync(passport.Id, "BirthPlacePassport", passport.BirthPlacePassport,
                () => passport.BirthPlacePassportEncrypted, value => passport.BirthPlacePassportEncrypted = value, null, null, ct);
            changed |= await ProtectStringAsync(passport.Id, "BirthPlaceFias", passport.BirthPlaceFias,
                () => passport.BirthPlaceFiasEncrypted, value => passport.BirthPlaceFiasEncrypted = value, null, null, ct);
            if (changed) updated++;
        }

        var lastProcessedId = passports[^1].Id;
        await WriteStateAsync(
            lastProcessedId,
            new BackfillMetrics(passports.Count, updated, lastProcessedId, false, DateTime.UtcNow),
            ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ClientPassportEncryptionBackfillResult(true, passports.Count, updated, lastProcessedId, false);
    }

    private async Task<bool> ProtectStringAsync(
        int passportId,
        string fieldName,
        string? plaintext,
        Func<string?> getCiphertext,
        Action<string> setCiphertext,
        Func<string?>? getBlindIndex,
        Action<string>? setBlindIndex,
        CancellationToken ct)
    {
        var ciphertext = getCiphertext();
        var blindIndex = getBlindIndex?.Invoke();
        if (plaintext is null)
        {
            if (ciphertext is not null || blindIndex is not null)
                throw new InvalidOperationException($"Поле ClientPassport.{fieldName} содержит зашифрованное значение без проверяемого источника.");
            return false;
        }

        var changed = false;
        var authenticatedContext = $"ClientPassport:{fieldName}:{passportId}";
        if (ciphertext is null)
        {
            ciphertext = await _encryptor.EncryptAsync(plaintext, authenticatedContext, ct);
            setCiphertext(ciphertext);
            changed = true;
        }

        var decrypted = await _encryptor.DecryptAsync(ciphertext, authenticatedContext, ct);
        if (!string.Equals(plaintext, decrypted, StringComparison.Ordinal))
            throw new InvalidOperationException($"Проверка decrypt/compare не пройдена для ClientPassport.{fieldName}.");

        if (getBlindIndex is not null && setBlindIndex is not null)
        {
            var expected = await _encryptor.ComputeBlindIndexAsync(plaintext, $"ClientPassport:{fieldName}", ct);
            if (blindIndex is null)
            {
                setBlindIndex(expected);
                changed = true;
            }
            else if (!string.Equals(blindIndex, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Blind index не соответствует ClientPassport.{fieldName}.");
            }
        }

        return changed;
    }

    private async Task<int> ReadCheckpointAsync(CancellationToken ct)
    {
        var raw = await _db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == CheckpointKey)
            .Select(item => item.Value)
            .SingleOrDefaultAsync(ct);
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    private async Task WriteStateAsync(int checkpoint, BackfillMetrics metrics, CancellationToken ct)
    {
        await UpsertSettingAsync(CheckpointKey, checkpoint.ToString(CultureInfo.InvariantCulture), ct);
        await UpsertSettingAsync(MetricsKey, JsonSerializer.Serialize(metrics), ct);
    }

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var setting = await _db.SystemSettings.SingleOrDefaultAsync(item => item.Key == key, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = DateTime.UtcNow;
    }

    private sealed record BackfillMetrics(
        int Scanned,
        int Updated,
        int LastProcessedId,
        bool CycleCompleted,
        DateTime CompletedAtUtc);
}
