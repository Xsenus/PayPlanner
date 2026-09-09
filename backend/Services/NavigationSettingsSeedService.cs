using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class NavigationSettingsSeedService
{
    public const string SettingKey = "navigation.sidebar.configuration.v1";

    private static readonly string[] DefaultOrder =
    [
        "workspace", "calendar", "notifications", "tasks", "notes", "reports", "calculator",
        "legalEntities", "clients", "cases", "contacts", "accounts", "installments",
        "money", "payments", "acts", "contracts", "payroll", "dictionaries", "users", "roles",
        "audit", "userActivity", "settings",
    ];

    private static readonly NavigationGroupSeed[] DefaultGroups =
    [
        new("group-client-work", "Работа с клиентами", ["legalEntities", "clients", "contacts"]),
        new("group-accounting", "Бухгалтерия", ["accounts", "acts", "contracts", "money", "payments"]),
        new("group-management", "Управление", ["users", "roles", "audit", "userActivity", "dictionaries", "settings"]),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> EnsureDefaultGroupsAsync(PaymentContext db, CancellationToken ct = default)
    {
        var setting = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == SettingKey, ct);
        StoredNavigationSettings? stored = null;
        if (setting is not null)
        {
            try
            {
                stored = JsonSerializer.Deserialize<StoredNavigationSettings>(setting.Value, JsonOptions);
            }
            catch (JsonException)
            {
                // A malformed legacy value is replaced with a valid default configuration.
            }

            if (stored?.Groups is { Count: > 0 })
            {
                var configuredOrder = (stored.Order ?? []).Distinct(StringComparer.Ordinal).ToList();
                var assignedItemIds = stored.Groups
                    .SelectMany(group => group.ItemIds)
                    .ToHashSet(StringComparer.Ordinal);
                var changed = EnsureRequiredSections(configuredOrder, assignedItemIds);
                if (!changed)
                {
                    return false;
                }

                setting.Value = JsonSerializer.Serialize(
                    new StoredNavigationSettings(
                        configuredOrder,
                        stored.Labels ?? new Dictionary<string, string>(),
                        stored.Groups),
                    JsonOptions);
                setting.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return true;
            }
        }

        var sourceOrder = stored?.Order is { Count: > 0 } ? stored.Order : DefaultOrder;
        var groupedItemIds = DefaultGroups.SelectMany(group => group.ItemIds).ToHashSet(StringComparer.Ordinal);
        var groupByItemId = DefaultGroups
            .SelectMany(group => group.ItemIds.Select(itemId => (itemId, group.Id)))
            .ToDictionary(pair => pair.itemId, pair => pair.Id, StringComparer.Ordinal);
        var insertedGroups = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var itemId in sourceOrder.Concat(DefaultOrder).Distinct(StringComparer.Ordinal))
        {
            if (groupByItemId.TryGetValue(itemId, out var groupId))
            {
                if (insertedGroups.Add(groupId)) order.Add(groupId);
                continue;
            }

            if (!groupedItemIds.Contains(itemId)) order.Add(itemId);
        }

        foreach (var group in DefaultGroups)
        {
            if (insertedGroups.Add(group.Id)) order.Add(group.Id);
        }

        var sourceItems = sourceOrder.ToHashSet(StringComparer.Ordinal);
        if (!sourceItems.Contains("workspace"))
        {
            order.Remove("workspace");
            order.Insert(0, "workspace");
        }
        order.Remove("payroll");
        InsertPayroll(order);

        var value = JsonSerializer.Serialize(
            new StoredNavigationSettings(order, stored?.Labels ?? new Dictionary<string, string>(), DefaultGroups),
            JsonOptions);
        var now = DateTime.UtcNow;
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool EnsureRequiredSections(List<string> order, IReadOnlySet<string> assignedItemIds)
    {
        var changed = false;
        if (!assignedItemIds.Contains("workspace") && !order.Contains("workspace", StringComparer.Ordinal))
        {
            order.Insert(0, "workspace");
            changed = true;
        }

        if (!assignedItemIds.Contains("payroll") && !order.Contains("payroll", StringComparer.Ordinal))
        {
            InsertPayroll(order);
            changed = true;
        }

        return changed;
    }

    private static void InsertPayroll(List<string> order)
    {
        var accountingGroupIndex = order.IndexOf("group-accounting");
        if (accountingGroupIndex >= 0)
        {
            order.Insert(accountingGroupIndex + 1, "payroll");
            return;
        }

        var financeIds = new[] { "accounts", "installments", "money", "payments", "acts", "contracts" };
        var financeIndex = order.FindLastIndex(financeIds.Contains);
        order.Insert(financeIndex >= 0 ? financeIndex + 1 : order.Count, "payroll");
    }

    private sealed record NavigationGroupSeed(string Id, string Name, IReadOnlyList<string> ItemIds);

    private sealed record StoredNavigationSettings(
        IReadOnlyList<string>? Order,
        IReadOnlyDictionary<string, string>? Labels,
        IReadOnlyList<NavigationGroupSeed>? Groups);
}
