using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

internal static class DepartmentVisibilityRecipients
{
    public static async Task<List<int>> FindAsync(
        PaymentContext db,
        IEnumerable<(int? Id, string? Name)> departments,
        CancellationToken cancellationToken = default)
    {
        var assignments = departments.ToArray();
        var departmentIds = assignments.Where(value => value.Id.HasValue).Select(value => value.Id!.Value).Distinct().ToArray();
        var departmentNames = assignments.Select(value => value.Name?.Trim() ?? string.Empty).Where(value => value.Length > 0).Distinct().ToArray();
        if (departmentIds.Length == 0 && departmentNames.Length == 0) return [];

        return await db.Users.AsNoTracking()
            .Where(member => member.IsActive && member.IsApproved && member.Role != null
                && ((member.DepartmentId.HasValue && departmentIds.Contains(member.DepartmentId.Value))
                    || (member.Department != null && departmentNames.Contains(member.Department.Trim())))
                && member.Role.Permissions.Any(permission => permission.CanView && permission.ViewScope.Trim().ToLower() == DataVisibilityScope.Department))
            .Select(member => member.Id).ToListAsync(cancellationToken);
    }
}
