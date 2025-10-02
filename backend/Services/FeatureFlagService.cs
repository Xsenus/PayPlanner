using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// Feature Flag Service for runtime feature toggling
/// Allows safe rollout and rollback of new functionality
/// </summary>
public class FeatureFlagService
{
    private readonly PaymentContext _context;
    private readonly Dictionary<string, bool> _cache;
    private DateTime _lastCacheRefresh;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(5);

    public FeatureFlagService(PaymentContext context)
    {
        _context = context;
        _cache = new Dictionary<string, bool>();
        _lastCacheRefresh = DateTime.MinValue;
    }

    public async Task<bool> IsEnabledAsync(string flagKey)
    {
        await RefreshCacheIfNeeded();

        if (_cache.TryGetValue(flagKey, out bool value))
        {
            return value;
        }

        return false;
    }

    public async Task<bool> EnableFlagAsync(string flagKey)
    {
        var flag = await _context.Set<FeatureFlag>()
            .FirstOrDefaultAsync(f => f.Key == flagKey);

        if (flag == null)
        {
            return false;
        }

        flag.Enabled = true;
        flag.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _cache[flagKey] = true;
        return true;
    }

    public async Task<bool> DisableFlagAsync(string flagKey)
    {
        var flag = await _context.Set<FeatureFlag>()
            .FirstOrDefaultAsync(f => f.Key == flagKey);

        if (flag == null)
        {
            return false;
        }

        flag.Enabled = false;
        flag.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _cache[flagKey] = false;
        return true;
    }

    public async Task<List<FeatureFlag>> GetAllFlagsAsync()
    {
        return await _context.Set<FeatureFlag>()
            .OrderBy(f => f.Key)
            .ToListAsync();
    }

    public void ClearCache()
    {
        _cache.Clear();
        _lastCacheRefresh = DateTime.MinValue;
    }

    private async Task RefreshCacheIfNeeded()
    {
        if (DateTime.UtcNow - _lastCacheRefresh < _cacheLifetime)
        {
            return;
        }

        var flags = await _context.Set<FeatureFlag>().ToListAsync();

        _cache.Clear();
        foreach (var flag in flags)
        {
            _cache[flag.Key] = flag.Enabled;
        }

        _lastCacheRefresh = DateTime.UtcNow;
    }
}
