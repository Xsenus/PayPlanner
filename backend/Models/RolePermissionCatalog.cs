using System.Collections.Generic;
using System.Linq;

namespace PayPlanner.Api.Models;

public static class RolePermissionCatalog
{
    public const string Calendar = "calendar";
    public const string Reports = "reports";
    public const string Calculator = "calculator";
    public const string LegalEntities = "legalEntities";
    public const string Clients = "clients";
    public const string Accounts = "accounts";
    public const string Acts = "acts";
    public const string Payments = "payments";
    public const string Contracts = "contracts";
    public const string Dictionaries = "dictionaries";
    public const string Roles = "roles";

    public static readonly string[] AllSections =
    [
        Calendar,
        Reports,
        Calculator,
        LegalEntities,
        Clients,
        Accounts,
        Acts,
        Payments,
        Contracts,
        Dictionaries,
        Roles
    ];

    public static IReadOnlyDictionary<string, RolePermissionValues> BuildDefaults(bool allowRoleManagement)
        => new Dictionary<string, RolePermissionValues>(StringComparer.OrdinalIgnoreCase)
        {
            [Calendar] = new(true, true, true, true, true, true),
            [Reports] = new(true, true, true, true, true, false),
            [Calculator] = new(true, true, true, true, true, false),
            [LegalEntities] = new(true, true, true, true, true, false),
            [Clients] = new(true, true, true, true, true, false),
            [Accounts] = new(true, true, true, true, true, false),
            [Acts] = new(true, true, true, true, true, false),
            [Payments] = new(true, true, true, true, true, false),
            [Contracts] = new(true, true, true, true, true, false),
            [Dictionaries] = new(true, true, true, true, true, false),
            [Roles] = new(
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                allowRoleManagement,
                false,
                false)
        };

    public static RolePermissionsDto CreateDto(bool allowRoleManagement)
    {
        var defaults = BuildDefaults(allowRoleManagement);

        return new RolePermissionsDto
        {
            Calendar = ToCalendar(defaults[Calendar]),
            Reports = ToSection(defaults[Reports]),
            Calculator = ToSection(defaults[Calculator]),
            LegalEntities = ToSection(defaults[LegalEntities]),
            Clients = ToSection(defaults[Clients]),
            Accounts = ToSection(defaults[Accounts]),
            Acts = ToSection(defaults[Acts]),
            Payments = ToSection(defaults[Payments]),
            Contracts = ToSection(defaults[Contracts]),
            Dictionaries = ToSection(defaults[Dictionaries]),
            Roles = ToSection(defaults[Roles])
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
                CanViewAnalytics = pair.Value.CanViewAnalytics
            })
            .ToList();

    public static MenuSectionPermissionsDto ToSection(RolePermissionValues values)
        => new()
        {
            CanView = values.CanView,
            CanCreate = values.CanCreate,
            CanEdit = values.CanEdit,
            CanDelete = values.CanDelete,
            CanExport = values.CanExport
        };

    public static CalendarPermissionsDto ToCalendar(RolePermissionValues values)
        => new()
        {
            CanView = values.CanView,
            CanCreate = values.CanCreate,
            CanEdit = values.CanEdit,
            CanDelete = values.CanDelete,
            CanExport = values.CanExport,
            CanViewAnalytics = values.CanViewAnalytics
        };
}

public sealed record RolePermissionValues(
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    bool CanExport,
    bool CanViewAnalytics);
