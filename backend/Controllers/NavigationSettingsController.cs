using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/navigation-settings")]
[Authorize]
public sealed class NavigationSettingsController : ControllerBase
{
    private const string SettingKey = NavigationSettingsSeedService.SettingKey;
    private const int MaximumLabelLength = 60;
    private const int MaximumGroupCount = 30;
    private const string GroupIdPrefix = "group-";

    private static readonly string[] DefaultOrder =
    [
        "calendar", "notifications", "tasks", "notes", "reports", "calculator",
        "legalEntities", "clients", "cases", "contacts", "accounts", "installments",
        "money", "payments", "acts", "contracts", "dictionaries", "users", "roles",
        "audit", "userActivity", "settings",
    ];

    private static readonly HashSet<string> KnownItemIds = new(DefaultOrder, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly RoleManagementAccessService _roleAccess;

    public NavigationSettingsController(PaymentContext db, RoleManagementAccessService roleAccess)
    {
        _db = db;
        _roleAccess = roleAccess;
    }

    [HttpGet]
    public async Task<ActionResult<NavigationSettingsDto>> Get(CancellationToken ct)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == SettingKey)
            .Select(item => new { item.Value, item.UpdatedAt })
            .SingleOrDefaultAsync(ct);

        if (setting is null)
        {
            return Ok(new NavigationSettingsDto(DefaultOrder, new Dictionary<string, string>(), [], null));
        }

        return Ok(Parse(setting.Value, setting.UpdatedAt));
    }

    [HttpPut]
    public async Task<ActionResult<NavigationSettingsDto>> Update(
        [FromBody] UpdateNavigationSettingsRequest request,
        CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin)
        {
            return Forbid();
        }

        var normalized = Normalize(request.Order, request.Labels, request.Groups, DateTime.UtcNow);
        var serialized = JsonSerializer.Serialize(
            new StoredNavigationSettings(normalized.Order, normalized.Labels, normalized.Groups),
            JsonOptions);
        var now = DateTime.UtcNow;
        var setting = await _db.SystemSettings.SingleOrDefaultAsync(item => item.Key == SettingKey, ct);
        if (setting is null)
        {
            setting = new SystemSetting
            {
                Key = SettingKey,
                Value = serialized,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = serialized;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(normalized with { UpdatedAt = setting.UpdatedAt });
    }

    private static NavigationSettingsDto Parse(string raw, DateTime updatedAt)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredNavigationSettings>(raw, JsonOptions);
            return Normalize(stored?.Order, stored?.Labels, stored?.Groups, updatedAt);
        }
        catch (JsonException)
        {
            return new NavigationSettingsDto(DefaultOrder, new Dictionary<string, string>(), [], updatedAt);
        }
    }

    private static NavigationSettingsDto Normalize(
        IEnumerable<string>? order,
        IReadOnlyDictionary<string, string>? labels,
        IEnumerable<NavigationGroupDto>? groups,
        DateTime? updatedAt)
    {
        var assignedItemIds = new HashSet<string>(StringComparer.Ordinal);
        var usedGroupIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedGroups = new List<NavigationGroupDto>();
        foreach (var group in groups ?? [])
        {
            if (group is null)
            {
                continue;
            }

            var groupId = group.Id?.Trim() ?? string.Empty;
            var groupName = group.Name?.Trim() ?? string.Empty;
            if (normalizedGroups.Count >= MaximumGroupCount
                || !IsValidGroupId(groupId)
                || !usedGroupIds.Add(groupId)
                || string.IsNullOrWhiteSpace(groupName))
            {
                continue;
            }

            var itemIds = (group.ItemIds ?? [])
                .Where(itemId => KnownItemIds.Contains(itemId) && assignedItemIds.Add(itemId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            normalizedGroups.Add(new NavigationGroupDto(
                groupId,
                groupName[..Math.Min(groupName.Length, MaximumLabelLength)],
                itemIds));
        }

        var knownRootIds = KnownItemIds
            .Where(itemId => !assignedItemIds.Contains(itemId))
            .Concat(normalizedGroups.Select(group => group.Id))
            .ToHashSet(StringComparer.Ordinal);
        var normalizedOrder = (order ?? [])
            .Where(knownRootIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        normalizedOrder.AddRange(DefaultOrder.Where(itemId => knownRootIds.Contains(itemId) && !normalizedOrder.Contains(itemId, StringComparer.Ordinal)));
        normalizedOrder.AddRange(normalizedGroups.Select(group => group.Id).Where(groupId => !normalizedOrder.Contains(groupId, StringComparer.Ordinal)));

        var normalizedLabels = (labels ?? new Dictionary<string, string>())
            .Where(pair => KnownItemIds.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Trim()[..Math.Min(pair.Value.Trim().Length, MaximumLabelLength)],
                StringComparer.Ordinal);

        return new NavigationSettingsDto(normalizedOrder, normalizedLabels, normalizedGroups, updatedAt);
    }

    private static bool IsValidGroupId(string groupId)
        => groupId.StartsWith(GroupIdPrefix, StringComparison.Ordinal)
            && groupId.Length is >= 8 and <= 80
            && groupId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private sealed record StoredNavigationSettings(
        IReadOnlyList<string>? Order,
        IReadOnlyDictionary<string, string>? Labels,
        IReadOnlyList<NavigationGroupDto>? Groups);
}

public sealed record NavigationGroupDto(
    string Id,
    string Name,
    IReadOnlyList<string>? ItemIds);

public sealed record NavigationSettingsDto(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<NavigationGroupDto> Groups,
    DateTime? UpdatedAt);

public sealed record UpdateNavigationSettingsRequest(
    IReadOnlyList<string>? Order,
    IReadOnlyDictionary<string, string>? Labels,
    IReadOnlyList<NavigationGroupDto>? Groups = null);
