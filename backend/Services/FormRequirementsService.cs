using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

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
            .ToListAsync(ct)
            .ContinueWith(task => task.Result
                .Select(key => NormalizeFieldKey(section, key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

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
        if (value is string s)
        {
            var trimmed = s.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return true;
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return !doc.RootElement.EnumerateArray().Any(e =>
                            e.ValueKind switch
                            {
                                JsonValueKind.String => !string.IsNullOrWhiteSpace(e.GetString()),
                                JsonValueKind.Null or JsonValueKind.Undefined => false,
                                _ => true,
                            });
                    }
                }
                catch
                {
                    // не JSON-массив, обрабатываем как обычную строку
                }
            }
            return false;
        }
        if (value is DateTime dt) return dt == default;
        if (value is int i) return i <= 0;
        if (value is decimal d) return d <= 0;
        if (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            var enumerable = ((System.Collections.IEnumerable)value).Cast<object?>();
            return !enumerable.Any(item => item switch
            {
                null => false,
                string str => !string.IsNullOrWhiteSpace(str),
                _ => true,
            });
        }
        return false;
    }

    private static string NormalizeFieldKey(string section, string fieldKey)
    {
        if (string.Equals(section, "clients-modal", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(fieldKey, "Name", StringComparison.OrdinalIgnoreCase)) return "FirstName";
            if (string.Equals(fieldKey, "Notes", StringComparison.OrdinalIgnoreCase)) return "Comment";
        }

        if (string.Equals(section, "clients-modal-legal", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fieldKey, "Notes", StringComparison.OrdinalIgnoreCase))
        {
            return "Comment";
        }

        return fieldKey;
    }
}
