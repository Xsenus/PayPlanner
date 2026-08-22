using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;

namespace PayPlanner.Api.Services;

public partial class AuthService
{
    private const string ProtectedSystemUserEmail = "ilel@list.ru";

    public static bool IsProtectedSystemUserEmail(string? email) =>
        string.Equals(
            NormalizeEmail(email ?? string.Empty),
            NormalizeEmail(ProtectedSystemUserEmail),
            StringComparison.Ordinal);

    private static bool IsProtectedSystemUser(User? user) =>
        user is not null && IsProtectedSystemUserEmail(user.Email);

    private static void EnsureProtectedUserManagedBySelf(User user, int actorUserId)
    {
        if (IsProtectedSystemUser(user) && user.Id != actorUserId)
        {
            throw new InvalidOperationException("ProtectedUserManagedOnlyBySelf");
        }
    }

    private static void EnsureProtectedUserNotDeleted(User user)
    {
        if (IsProtectedSystemUser(user))
        {
            throw new InvalidOperationException("ProtectedUserCannotBeDeleted");
        }
    }

    private static void EnsureProtectedUserEmailUnchanged(User user, string? requestedEmail)
    {
        if (!IsProtectedSystemUser(user) || requestedEmail is null)
        {
            return;
        }

        if (!string.Equals(NormalizeEmail(requestedEmail), NormalizeEmail(user.Email), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ProtectedUserEmailChangeForbidden");
        }
    }

    private static void EnsureProtectedEmailReserved(User? currentUser, string? requestedEmail)
    {
        if (requestedEmail is null || !IsProtectedSystemUserEmail(requestedEmail))
        {
            return;
        }

        if (currentUser is null || !IsProtectedSystemUser(currentUser))
        {
            throw new InvalidOperationException("ProtectedUserEmailReserved");
        }
    }

    private static void EnsureProtectedUserAdministrativeFieldsUnchanged(User user, UpdateUserRequest request)
    {
        if (!IsProtectedSystemUser(user))
        {
            return;
        }

        if ((request.RoleId.HasValue && request.RoleId.Value != user.RoleId)
            || (request.IsActive.HasValue && request.IsActive.Value != user.IsActive)
            || (request.IsEmployee.HasValue && request.IsEmployee.Value != user.IsEmployee)
            || (request.IsExternalContractor.HasValue && request.IsExternalContractor.Value != user.IsExternalContractor)
            || (request.DepartmentId.HasValue && request.DepartmentId.Value != user.DepartmentId)
            || (request.Department is not null && NormalizeOptionalValue(request.Department) != user.Department)
            || (request.RequiresFinancialActionReason.HasValue && request.RequiresFinancialActionReason.Value != user.RequiresFinancialActionReason)
            || (request.ClearEmploymentStartDate && user.EmploymentStartDate.HasValue)
            || (request.ClearEmploymentEndDate && user.EmploymentEndDate.HasValue)
            || (request.EmploymentStartDate.HasValue && request.EmploymentStartDate != user.EmploymentStartDate)
            || (request.EmploymentEndDate.HasValue && request.EmploymentEndDate != user.EmploymentEndDate)
            || (request.CompanyIds is not null
                && !request.CompanyIds
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .SequenceEqual(
                        user.CompanyAccesses
                            .Select(access => access.LegalEntityId)
                            .Distinct()
                            .OrderBy(id => id))))
        {
            throw new InvalidOperationException("ProtectedUserAdministrativeFieldsLocked");
        }
    }

    private static void EnsureProtectedUserRemainsPrimary(User targetUser, IEnumerable<User> sourceUsers)
    {
        var mergeContainsProtectedUser = IsProtectedSystemUser(targetUser) || sourceUsers.Any(IsProtectedSystemUser);
        if (mergeContainsProtectedUser && !IsProtectedSystemUser(targetUser))
        {
            throw new InvalidOperationException("ProtectedUserMustRemainPrimary");
        }
    }

    private static void ApplyProtectedUserInvariants(User user)
    {
        if (!IsProtectedSystemUser(user))
        {
            return;
        }

        user.IsActive = true;
        user.IsApproved = true;
    }
}
