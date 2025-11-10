using System;
using System.Collections.Generic;
using System.Linq;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class MenuSectionPermissionsDto
{
    public bool CanView { get; set; } = true;
    public bool CanCreate { get; set; } = true;
    public bool CanEdit { get; set; } = true;
    public bool CanDelete { get; set; } = true;
    public bool CanExport { get; set; } = true;
}

public class CalendarPermissionsDto : MenuSectionPermissionsDto
{
    public bool CanViewAnalytics { get; set; } = true;
}

public class RolePermissionsDto
{
    public CalendarPermissionsDto Calendar { get; set; } = new();
    public MenuSectionPermissionsDto Reports { get; set; } = new();
    public MenuSectionPermissionsDto Calculator { get; set; } = new();
    public MenuSectionPermissionsDto Clients { get; set; } = new();
    public MenuSectionPermissionsDto Accounts { get; set; } = new();
    public MenuSectionPermissionsDto Acts { get; set; } = new();
    public MenuSectionPermissionsDto Contracts { get; set; } = new();
    public MenuSectionPermissionsDto Dictionaries { get; set; } = new();

    public static RolePermissionsDto CreateDefault()
    {
        return new RolePermissionsDto();
    }

    public void ApplyFrom(RolePermissionsDto? source)
    {
        if (source is null)
        {
            return;
        }

        CopySection(Calendar, source.Calendar);
        CopySection(Reports, source.Reports);
        CopySection(Calculator, source.Calculator);
        CopySection(Clients, source.Clients);
        CopySection(Accounts, source.Accounts);
        CopySection(Acts, source.Acts);
        CopySection(Contracts, source.Contracts);
        CopySection(Dictionaries, source.Dictionaries);
    }

    private static void CopySection(MenuSectionPermissionsDto target, MenuSectionPermissionsDto? source)
    {
        if (source is null)
        {
            return;
        }

        target.CanView = source.CanView;
        target.CanCreate = source.CanCreate;
        target.CanEdit = source.CanEdit;
        target.CanDelete = source.CanDelete;
        target.CanExport = source.CanExport;

        if (target is CalendarPermissionsDto tc && source is CalendarPermissionsDto sc)
        {
            tc.CanViewAnalytics = sc.CanViewAnalytics;
        }
    }

    public IEnumerable<(string Section, MenuSectionPermissionsDto Permissions)> EnumerateSections()
    {
        yield return ("calendar", Calendar);
        yield return ("reports", Reports);
        yield return ("calculator", Calculator);
        yield return ("clients", Clients);
        yield return ("accounts", Accounts);
        yield return ("acts", Acts);
        yield return ("contracts", Contracts);
        yield return ("dictionaries", Dictionaries);
    }

    public MenuSectionPermissionsDto GetSection(string section)
    {
        return section.ToLowerInvariant() switch
        {
            "calendar" => Calendar,
            "reports" => Reports,
            "calculator" => Calculator,
            "clients" => Clients,
            "accounts" => Accounts,
            "acts" => Acts,
            "contracts" => Contracts,
            "dictionaries" => Dictionaries,
            _ => Calendar,
        };
    }

    public static RolePermissionsDto FromEntities(IEnumerable<RolePermission> permissions)
    {
        var dto = CreateDefault();
        foreach (var permission in permissions)
        {
            var section = dto.GetSection(permission.Section);
            section.CanView = permission.CanView;
            section.CanCreate = permission.CanCreate;
            section.CanEdit = permission.CanEdit;
            section.CanDelete = permission.CanDelete;
            section.CanExport = permission.CanExport;

            if (section is CalendarPermissionsDto calendar)
            {
                calendar.CanViewAnalytics = permission.CanViewAnalytics ?? calendar.CanViewAnalytics;
            }
        }

        return dto;
    }
}
