using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Companies;

/// <summary>Reads the existing own-company registry without guessing an owner.</summary>
public static class OwnCompanyRegistry
{
    private const string MainIdsKey = "legalEntities.mainCompanyIds";
    private const string LegacyIdKey = "legalEntities.mainCompanyId";

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
