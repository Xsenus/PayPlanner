using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public interface IFormRequirementsService
{
    Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default);
    Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default);
}

public class FormRequirementsService : IFormRequirementsService
{
    private readonly PaymentContext _db;

    public FormRequirementsService(PaymentContext db) => _db = db;

    public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
        => _db.FormFieldRequirements.AsNoTracking()
            .Where(x => x.Section == section && x.IsRequired)
            .Select(x => x.FieldKey)
            .ToListAsync(ct);

    public async Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
    {
        var requiredKeys = await GetRequiredFieldsAsync(section, ct);
        if (requiredKeys.Count == 0) return new List<string>();

        var properties = model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var key in requiredKeys)
        {
            if (!properties.TryGetValue(key, out var prop))
            {
                missing.Add(key);
                continue;
            }

            var value = prop.GetValue(model);
            if (IsMissing(value, prop.PropertyType)) missing.Add(key);
        }

        return missing;
    }

    private static bool IsMissing(object? value, Type type)
    {
        if (value is null) return true;
        if (value is string s) return string.IsNullOrWhiteSpace(s);
        if (value is DateTime dt) return dt == default;
        if (value is int i) return i <= 0;
        if (value is decimal d) return d <= 0;
        if (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            var enumerable = (System.Collections.IEnumerable)value;
            return !enumerable.Cast<object>().Any();
        }
        return false;
    }
}
