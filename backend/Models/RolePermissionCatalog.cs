using System.Collections.Generic;
using System.Linq;

namespace PayPlanner.Api.Models;

public static class RolePermissionCatalog
{
    public const string Calendar = "calendar";
    public const string Tasks = "tasks";
    public const string Reports = "reports";
    public const string Calculator = "calculator";
    public const string LegalEntities = "legalEntities";
    public const string Clients = "clients";
    public const string Cases = "cases";
    public const string Accounts = "accounts";
    public const string Acts = "acts";
    public const string Payments = "payments";
    public const string Contracts = "contracts";
    public const string Dictionaries = "dictionaries";
    public const string Notifications = "notifications";
    public const string Roles = "roles";
    public const string Audit = "audit";
    public const string YandexDisk = "yandexDisk";
    public const string S3Storage = "s3Storage";

    public static readonly string[] AllSections =
    [
        Calendar,
        Tasks,
        Reports,
        Calculator,
        LegalEntities,
        Clients,
        Cases,
        Accounts,
        Acts,
        Payments,
        Contracts,
        Dictionaries,
        Notifications,
        Roles,
        Audit,
        YandexDisk,
        S3Storage
    ];

    public static readonly string[] DataScopedSections =
    [
        Tasks,
        LegalEntities,
        Clients,
        Cases,
        Accounts,
        Acts,
        Payments,
        Contracts,
    ];

    public static bool HasDataScope(string section)
        => DataScopedSections.Contains(section, StringComparer.OrdinalIgnoreCase);

    public static string? GetFallbackSection(string section)
        => string.Equals(section, Cases, StringComparison.OrdinalIgnoreCase)
            ? Clients
            : null;

    public static string GetDefaultViewScope(string section, bool allowRoleManagement)
        => allowRoleManagement || !HasDataScope(section)
            ? DataVisibilityScope.All
            : DataVisibilityScope.Own;

    public static IReadOnlyDictionary<string, RolePermissionValues> BuildDefaults(bool allowRoleManagement)
        => new Dictionary<string, RolePermissionValues>(StringComparer.OrdinalIgnoreCase)
        {
            [Calendar] = new(true, true, true, true, true, true, GetDefaultViewScope(Calendar, allowRoleManagement)),
            [Tasks] = new(true, true, true, true, true, false, GetDefaultViewScope(Tasks, allowRoleManagement)),
            [Reports] = new(true, true, true, true, true, false, GetDefaultViewScope(Reports, allowRoleManagement)),
            [Calculator] = new(true, true, true, true, true, false, GetDefaultViewScope(Calculator, allowRoleManagement)),
            [LegalEntities] = new(true, true, true, true, true, false, GetDefaultViewScope(LegalEntities, allowRoleManagement)),
            [Clients] = new(true, true, true, true, true, false, GetDefaultViewScope(Clients, allowRoleManagement)),
            [Cases] = new(true, true, true, true, true, false, GetDefaultViewScope(Cases, allowRoleManagement)),
            [Accounts] = new(true, true, true, true, true, false, GetDefaultViewScope(Accounts, allowRoleManagement)),
            [Acts] = new(true, true, true, true, true, false, GetDefaultViewScope(Acts, allowRoleManagement)),
            [Payments] = new(true, true, true, true, true, false, GetDefaultViewScope(Payments, allowRoleManagement)),
            [Contracts] = new(true, true, true, true, true, false, GetDefaultViewScope(Contracts, allowRoleManagement)),
            [Dictionaries] = new(true, true, true, true, true, false, GetDefaultViewScope(Dictionaries, allowRoleManagement)),
            [Notifications] = new(
                allowRoleManagement,
                false,
                allowRoleManagement,
                false,
                false,
                false,
                GetDefaultViewScope(Notifications, allowRoleManagement)),
            [Roles] = new(
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                false,
                false,
                GetDefaultViewScope(Roles, allowRoleManagement)),
            [Audit] = new(
                allowRoleManagement,
                false,
                allowRoleManagement,
                false,
                false,
                false,
                GetDefaultViewScope(Audit, allowRoleManagement)),
            [YandexDisk] = new(
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                false,
                GetDefaultViewScope(YandexDisk, allowRoleManagement)),
            [S3Storage] = new(
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                false,
                GetDefaultViewScope(S3Storage, allowRoleManagement))
        };

    public static RolePermissionsDto CreateDto(bool allowRoleManagement)
    {
        var defaults = BuildDefaults(allowRoleManagement);

        return new RolePermissionsDto
        {
            Calendar = ToCalendar(defaults[Calendar]),
            Tasks = ToSection(defaults[Tasks]),
            Reports = ToSection(defaults[Reports]),
            Calculator = ToSection(defaults[Calculator]),
            LegalEntities = ToSection(defaults[LegalEntities]),
            Clients = ToSection(defaults[Clients]),
            Cases = ToSection(defaults[Cases]),
            Accounts = ToSection(defaults[Accounts]),
            Acts = ToSection(defaults[Acts]),
            Payments = ToSection(defaults[Payments]),
            Contracts = ToSection(defaults[Contracts]),
            Dictionaries = ToSection(defaults[Dictionaries]),
            Notifications = ToSection(defaults[Notifications]),
            Roles = ToSection(defaults[Roles]),
            Audit = ToSection(defaults[Audit]),
            YandexDisk = ToSection(defaults[YandexDisk]),
            S3Storage = ToSection(defaults[S3Storage])
        };
    }

    public static List<RolePermission> CreateEntities(int roleId, bool allowRoleManagement)
        => BuildDefaults(allowRoleManagement)
            .Select(pair => new RolePermission
            {
                RoleId = roleId,
                Section = pair.Key,
                CanView = pair.Value.CanView,
                CanCreate = pair.Value.CanCreate,
                CanEdit = pair.Value.CanEdit,
                CanDelete = pair.Value.CanDelete,
                CanExport = pair.Value.CanExport,
                CanViewAnalytics = pair.Value.CanViewAnalytics,
                ViewScope = pair.Value.ViewScope
            })
            .ToList();

    public static MenuSectionPermissionsDto ToSection(RolePermissionValues values)
        => new()
        {
            CanView = values.CanView,
            CanCreate = values.CanCreate,
            CanEdit = values.CanEdit,
            CanDelete = values.CanDelete,
            CanExport = values.CanExport,
            ViewScope = values.ViewScope
        };

    public static CalendarPermissionsDto ToCalendar(RolePermissionValues values)
        => new()
        {
            CanView = values.CanView,
            CanCreate = values.CanCreate,
            CanEdit = values.CanEdit,
            CanDelete = values.CanDelete,
            CanExport = values.CanExport,
            CanViewAnalytics = values.CanViewAnalytics,
            ViewScope = values.ViewScope
        };
}

public sealed record RolePermissionValues(
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    bool CanExport,
    bool CanViewAnalytics,
    string ViewScope);
