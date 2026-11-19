using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Notifications;

public class ContractExpirationRecipientSettings
{
    public const string SettingKey = "notifications.contractExpiration.recipients";
    public const int MaximumSelections = 500;
    private static readonly JsonSerializerOptions StorageJsonOptions = new(JsonSerializerDefaults.Web);

    public bool DepartmentManagersEnabled { get; set; }
    public int[] RoleIds { get; set; } = [];
    public int[] UserIds { get; set; } = [];
    public List<ContractExpirationDepartmentManager> DepartmentManagers { get; set; } = [];
    [JsonIgnore]
    public string? RawValue { get; set; }

    public static async Task<ContractExpirationRecipientSettings> LoadAsync(PaymentContext db, CancellationToken ct = default)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);
        return Parse(raw);
    }

    public static ContractExpirationRecipientSettings Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new() { RawValue = raw };
        try
        {
            var value = JsonSerializer.Deserialize<ContractExpirationRecipientSettings>(raw, StorageJsonOptions);
            if (!TryNormalize(value, out var normalized, out _)) return new() { RawValue = raw };
            normalized.RawValue = raw;
            return normalized;
        }
        catch (JsonException)
        {
            return new() { RawValue = raw };
        }
    }

    public static string Serialize(ContractExpirationRecipientSettings value)
    {
        if (!TryNormalize(value, out var normalized, out var error))
            throw new ArgumentException(error, nameof(value));
        return JsonSerializer.Serialize(normalized, StorageJsonOptions);
    }

    public static bool TryNormalize(ContractExpirationRecipientSettings? value,
        out ContractExpirationRecipientSettings normalized, out string? error)
    {
        normalized = new();
        error = null;
        if (value is null || value.RoleIds is null || value.UserIds is null || value.DepartmentManagers is null)
        {
            error = "Передайте все списки получателей.";
            return false;
        }
        if (value.RoleIds.Length > MaximumSelections || value.UserIds.Length > MaximumSelections
            || value.DepartmentManagers.Count > MaximumSelections)
        {
            error = "В каждом списке можно выбрать не более 500 записей.";
            return false;
        }
        if (value.RoleIds.Any(id => id <= 0) || value.UserIds.Any(id => id <= 0)
            || value.DepartmentManagers.Any(item => item is null || item.DepartmentId <= 0 || item.ManagerUserId <= 0))
        {
            error = "Идентификаторы получателей и подразделений должны быть положительными.";
            return false;
        }
        if (value.DepartmentManagers.Select(item => item.DepartmentId).Distinct().Count() != value.DepartmentManagers.Count)
        {
            error = "Укажите одного руководителя для каждого выбранного подразделения.";
            return false;
        }
        normalized = new ContractExpirationRecipientSettings
        {
            DepartmentManagersEnabled = value.DepartmentManagersEnabled,
            RoleIds = value.RoleIds.Distinct().OrderBy(id => id).ToArray(),
            UserIds = value.UserIds.Distinct().OrderBy(id => id).ToArray(),
            DepartmentManagers = value.DepartmentManagers.OrderBy(item => item.DepartmentId)
                .Select(item => new ContractExpirationDepartmentManager
                {
                    DepartmentId = item.DepartmentId,
                    ManagerUserId = item.ManagerUserId
                }).ToList()
        };
        return true;
    }
}

public class ContractExpirationDepartmentManager
{
    public int DepartmentId { get; set; }
    public int ManagerUserId { get; set; }
}
