using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Services.Companies;

public sealed record CompanyDocumentImagePrintDefaults(
    DocumentImagePrintOptions? Invoice, DocumentImagePrintOptions? Act, DocumentImagePrintOptions? Contract)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Invoice is null && Act is null && Contract is null;
}

public sealed record CompanyDocumentImagePrintDefaultsState(CompanyDocumentImagePrintDefaults? Value, bool Invalid = false, string? Version = null);

/// <summary>Настройки компании в существующем хранилище; запись участвует в SaveChanges профиля.</summary>
public sealed class CompanyDocumentImagePrintDefaultsService(PaymentContext db)
{
    public const string KeyPrefix = "legalEntities.documentImagePrintDefaults.";
    public const int MaximumLength = 1024;
    public static string Key(int companyId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(companyId);
        return KeyPrefix + companyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool ShouldWrite(bool? changed) => changed == true;

    public async Task<CompanyDocumentImagePrintDefaultsState> ReadAsync(int companyId, CancellationToken ct = default)
    {
        var key = Key(companyId);
        var value = await db.SystemSettings.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(ct);
        return Parse(value) with { Version = VersionOf(companyId, value) };
    }

    public static bool IsValidVersion(string? version) => version is { Length: 64 } && version.All(Uri.IsHexDigit);

    private static string VersionOf(int companyId, string? value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Key(companyId) + (value is null ? ":missing" : ":value:")));
        if (value is not null)
        {
            // Даже повреждённая большая запись не требует второго большого буфера.
            var encoder = Encoding.UTF8.GetEncoder();
            Span<byte> bytes = stackalloc byte[2048];
            var remaining = value.AsSpan();
            do
            {
                encoder.Convert(remaining, bytes, true, out var used, out var written, out _);
                hash.AppendData(bytes[..written]);
                remaining = remaining[used..];
            } while (!remaining.IsEmpty);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public Task<T> EditAsync<T>(int companyId, string expectedVersion, Func<Task<T>> edit, CancellationToken ct)
        => DocumentSellerLease.RunAsync(db, async lease =>
        {
            lease.AdmitCleanTracker();
            // Строка компании существует и при отсутствии/сбросе настройки: блокируем также первый INSERT.
            if (db.Database.IsNpgsql())
            {
                var ids = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);
                if (ids.Length != 1) throw new SellerSnapshotConflictException();
            }
            else if (!await db.LegalEntities.AsNoTracking().AnyAsync(row => row.Id == companyId, ct))
                throw new SellerSnapshotConflictException();
            var current = await ReadAsync(companyId, ct);
            if (!string.Equals(expectedVersion, current.Version, StringComparison.OrdinalIgnoreCase)) throw new SellerSnapshotConflictException();
            var tracked = db.SystemSettings.Local.SingleOrDefault(row => row.Key == Key(companyId));
            if (tracked is not null) await db.Entry(tracked).ReloadAsync(ct);
            return await edit();
        }, ct);

    // Не сохраняет чужие pending entries: вызывающий сохраняет настройки и профиль одним SaveChanges/транзакцией.
    public async Task StageAsync(int companyId, CompanyDocumentImagePrintDefaults? value, bool? changed, CancellationToken ct = default)
    {
        var key = Key(companyId);
        ct.ThrowIfCancellationRequested();
        if (!ShouldWrite(changed)) return;
        var existing = db.SystemSettings.Local.SingleOrDefault(row => row.Key == key)
            ?? await db.SystemSettings.SingleOrDefaultAsync(row => row.Key == key, ct);
        if (value is null || value.IsEmpty)
        {
            if (existing is not null) db.SystemSettings.Remove(existing);
            return;
        }
        var json = Serialize(value);
        if (existing is null)
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = json });
        else
        {
            var entry = db.Entry(existing);
            if (entry.State == EntityState.Deleted)
                entry.State = entry.Property(row => row.Value).OriginalValue == existing.Value ? EntityState.Unchanged : EntityState.Modified;
            if (existing.Value == json) return;
            existing.Value = json;
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }

    public static string Serialize(CompanyDocumentImagePrintDefaults value) => new JsonObject
    {
        ["version"] = 1,
        ["invoice"] = ToNode(value.Invoice),
        ["act"] = ToNode(value.Act),
        ["contract"] = ToNode(value.Contract),
    }.ToJsonString();

    private static JsonObject? ToNode(DocumentImagePrintOptions? value) => value is null ? null : new JsonObject
    {
        ["includeLogo"] = value.IncludeLogo,
        ["includeSeal"] = value.IncludeSeal,
        ["includeSignature"] = value.IncludeSignature,
    };

    public static CompanyDocumentImagePrintDefaultsState Parse(string? json)
    {
        if (json is null) return new(null);
        if (json.Length == 0 || json.Length > MaximumLength) return new(null, true);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var number) || number != 1
                || !TryRead(root, "invoice", out var invoice) || !TryRead(root, "act", out var act) || !TryRead(root, "contract", out var contract))
                return new(null, true);
            var value = new CompanyDocumentImagePrintDefaults(invoice, act, contract);
            return new(value.IsEmpty ? null : value);
        }
        catch (JsonException) { return new(null, true); }
        catch (InvalidOperationException) { return new(null, true); }
    }

    private static bool TryRead(JsonElement root, string key, out DocumentImagePrintOptions? value)
    {
        value = null;
        if (!root.TryGetProperty(key, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return true;
        if (item.ValueKind != JsonValueKind.Object || !Boolean(item, "includeLogo", out var logo)
            || !Boolean(item, "includeSeal", out var seal) || !Boolean(item, "includeSignature", out var signature)) return false;
        value = new(logo, seal, signature);
        return true;
    }

    private static bool Boolean(JsonElement item, string key, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(key, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }
}
