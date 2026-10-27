using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

/// <summary>Reads the existing own-company registry without guessing an owner.</summary>
public static class OwnCompanyRegistry
{
    private const string MainIdsKey = "legalEntities.mainCompanyIds";
    private const string LegacyIdKey = "legalEntities.mainCompanyId";

    internal sealed record State(string? MainIds, string? LegacyId, string? DefaultId)
    {
        public HashSet<int> OwnIds()
        {
            var ids = new HashSet<int>();
            try { foreach (var id in JsonSerializer.Deserialize<List<int>>(MainIds ?? "[]") ?? []) if (id > 0) ids.Add(id); }
            catch (JsonException) { }
            if (ids.Count == 0 && int.TryParse(LegacyId, out var legacy) && legacy > 0) ids.Add(legacy);
            return ids;
        }
        public int? SelectedDefaultId()
        {
            if (int.TryParse(DefaultId, out var selected) && selected > 0) return selected;
            if (int.TryParse(LegacyId, out var legacy) && legacy > 0) return legacy;
            try { return JsonSerializer.Deserialize<List<int>>(MainIds ?? "[]")?.FirstOrDefault(id => id > 0); }
            catch (JsonException) { return null; }
        }
    }

    internal static async Task<State> ReadStateAsync(PaymentContext db, CancellationToken ct)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == MainIdsKey || item.Key == LegacyIdKey || item.Key == "legalEntities.defaultMainCompanyId")
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);
        return new(values.GetValueOrDefault(MainIdsKey), values.GetValueOrDefault(LegacyIdKey),
            values.GetValueOrDefault("legalEntities.defaultMainCompanyId"));
    }

    public static async Task<LegalEntity?> LoadDefaultAsync(PaymentContext db, CancellationToken ct = default)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == MainIdsKey || item.Key == LegacyIdKey || item.Key == "legalEntities.defaultMainCompanyId")
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);
        int? id = null;
        if (values.TryGetValue("legalEntities.defaultMainCompanyId", out var rawDefault) && int.TryParse(rawDefault, out var parsedDefault) && parsedDefault > 0)
            id = parsedDefault;
        else if (values.TryGetValue(LegacyIdKey, out var legacy) && int.TryParse(legacy, out var parsedLegacy) && parsedLegacy > 0)
            id = parsedLegacy;
        else if (values.TryGetValue(MainIdsKey, out var rawIds))
        {
            try { id = JsonSerializer.Deserialize<List<int>>(rawIds)?.FirstOrDefault(value => value > 0); }
            catch (JsonException) { }
        }
        return id is > 0 ? await db.LegalEntities.AsNoTracking().Include(company => company.Addresses)
            .FirstOrDefaultAsync(company => company.Id == id, ct) : null;
    }

    public static async Task<HashSet<int>> LoadIdsAsync(PaymentContext db, CancellationToken ct = default)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == MainIdsKey || item.Key == LegacyIdKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);
        var ids = new HashSet<int>();
        if (values.TryGetValue(MainIdsKey, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                foreach (var id in JsonSerializer.Deserialize<List<int>>(raw) ?? [])
                    if (id > 0) ids.Add(id);
            }
            catch (JsonException)
            {
                // Match the current DocumentSellerService fallback exactly.
            }
        }
        if (ids.Count == 0 && values.TryGetValue(LegacyIdKey, out var legacy)
            && int.TryParse(legacy, out var legacyId) && legacyId > 0)
            ids.Add(legacyId);
        return ids;
    }
}
