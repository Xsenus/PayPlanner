using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public partial class AuthService
{
    private IQueryable<User> QueryUsersWithRelations()
        => _context.Users
            .Include(x => x.Role)
            .Include(x => x.ExternalIdentities);

    private static ExternalIdentityDto MapExternalIdentity(UserExternalIdentity identity) => new()
    {
        Id = identity.Id,
        Provider = identity.Provider,
        ProviderUserId = identity.ProviderUserId,
        ProviderEmail = identity.ProviderEmail,
        ProviderUsername = identity.ProviderUsername,
        DisplayName = identity.DisplayName,
        AvatarUrl = identity.AvatarUrl,
        TelegramUserId = identity.TelegramUserId,
        ChatId = identity.ChatId,
        VerifiedAt = identity.VerifiedAt,
        LastUsedAt = identity.LastUsedAt,
        CreatedAt = identity.CreatedAt
    };

    private static List<ExternalIdentityDto> MapExternalIdentities(IEnumerable<UserExternalIdentity>? identities)
        => (identities ?? Array.Empty<UserExternalIdentity>())
            .OrderBy(x => x.Provider)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .Select(MapExternalIdentity)
            .ToList();

    private static string? NormalizeOptionalValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BuildFallbackUserFullName(User user)
    {
        var parts = new[] { user.LastName, user.FirstName, user.MiddleName }
            .Select(NormalizeOptionalValue)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (parts.Length > 0)
        {
            return string.Join(' ', parts);
        }

        var email = NormalizeOptionalValue(user.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var atIndex = email.IndexOf('@');
            return atIndex > 0 ? email[..atIndex] : email;
        }

        return "Пользователь";
    }

    private async Task ApplyUserChangesAsync(
        User user,
        UpdateUserRequest request,
        bool allowAdministrativeFields,
        CancellationToken cancellationToken = default)
    {
        if (request.Email is not null)
        {
            var normalizedEmail = NormalizeEmail(request.Email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                throw new ArgumentException("Email is required.");
            }

            var emailExists = await _context.Users
                .AsNoTracking()
                .AnyAsync(x => x.Id != user.Id && x.Email == normalizedEmail, cancellationToken);
            if (emailExists)
            {
                throw new InvalidOperationException("UserAlreadyExists");
            }

            user.Email = normalizedEmail;
        }

        if (request.FullName is not null)
        {
            user.FullName = NormalizeOptionalValue(request.FullName) ?? string.Empty;
        }

        if (request.FirstName is not null)
        {
            user.FirstName = NormalizeOptionalValue(request.FirstName);
        }

        if (request.LastName is not null)
        {
            user.LastName = NormalizeOptionalValue(request.LastName);
        }

        if (request.MiddleName is not null)
        {
            user.MiddleName = NormalizeOptionalValue(request.MiddleName);
        }

        if (request.PhotoUrl is not null)
        {
            user.PhotoUrl = NormalizeOptionalValue(request.PhotoUrl);
        }

        if (request.PhoneNumber is not null)
        {
            user.PhoneNumber = NormalizeOptionalValue(request.PhoneNumber);
        }

        if (request.WhatsApp is not null)
        {
            user.WhatsApp = NormalizeOptionalValue(request.WhatsApp);
        }

        if (request.Telegram is not null)
        {
            user.Telegram = NormalizeOptionalValue(request.Telegram);
        }

        if (request.Instagram is not null)
        {
            user.Instagram = NormalizeOptionalValue(request.Instagram);
        }

        if (request.Messenger is not null)
        {
            user.Messenger = NormalizeOptionalValue(request.Messenger);
        }

        if (request.Viber is not null)
        {
            user.Viber = NormalizeOptionalValue(request.Viber);
        }

        if (request.DateOfBirth.HasValue)
        {
            if (request.DateOfBirth > DateTime.UtcNow.Date)
            {
                throw new ArgumentException("DateOfBirth cannot be in the future.");
            }

            user.DateOfBirth = request.DateOfBirth;
        }

        if (allowAdministrativeFields)
        {
            if (request.EmploymentStartDate.HasValue)
            {
                user.EmploymentStartDate = request.EmploymentStartDate;
            }

            if (request.EmploymentEndDate.HasValue)
            {
                user.EmploymentEndDate = request.EmploymentEndDate;
            }

            if (user.EmploymentStartDate.HasValue
                && user.EmploymentEndDate.HasValue
                && user.EmploymentStartDate > user.EmploymentEndDate)
            {
                throw new ArgumentException("EmploymentStartDate cannot be after EmploymentEndDate.");
            }

            if (request.IsEmployee.HasValue)
            {
                user.IsEmployee = request.IsEmployee.Value;
            }

            if (request.RoleId.HasValue && request.RoleId.Value != user.RoleId)
            {
                var roleExists = await _context.Roles
                    .AsNoTracking()
                    .AnyAsync(x => x.Id == request.RoleId.Value, cancellationToken);
                if (!roleExists)
                {
                    throw new InvalidOperationException("RoleNotFound");
                }

                user.RoleId = request.RoleId.Value;
            }

            if (request.IsActive.HasValue)
            {
                user.IsActive = request.IsActive.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(user.FullName))
        {
            user.FullName = BuildFallbackUserFullName(user);
        }

        user.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<UserDto?> UpdateProfileAsync(
        int id,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await QueryUsersWithRelations()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        await ApplyUserChangesAsync(
            user,
            new UpdateUserRequest
            {
                Email = request.Email,
                FullName = request.FullName,
                FirstName = request.FirstName,
                LastName = request.LastName,
                MiddleName = request.MiddleName,
                DateOfBirth = request.DateOfBirth,
                PhotoUrl = request.PhotoUrl,
                PhoneNumber = request.PhoneNumber,
                WhatsApp = request.WhatsApp,
                Telegram = request.Telegram,
                Instagram = request.Instagram,
                Messenger = request.Messenger,
                Viber = request.Viber
            },
            allowAdministrativeFields: false,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(user);
    }

    public async Task<UserDto> MergeUsersAsync(
        MergeUsersRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceUserIds = (request.SourceUserIds ?? new List<int>())
            .Where(x => x > 0)
            .Distinct()
            .Where(x => x != request.TargetUserId)
            .ToList();

        if (request.TargetUserId <= 0 || sourceUserIds.Count == 0)
        {
            throw new InvalidOperationException("Для объединения нужно выбрать основной профиль и хотя бы один профиль-источник.");
        }

        var allUserIds = sourceUserIds
            .Append(request.TargetUserId)
            .Distinct()
            .ToList();

        var users = await QueryUsersWithRelations()
            .Where(x => allUserIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var targetUser = users.FirstOrDefault(x => x.Id == request.TargetUserId)
            ?? throw new InvalidOperationException("Основной пользователь не найден.");
        var sourceUsers = users
            .Where(x => sourceUserIds.Contains(x.Id))
            .ToList();

        if (sourceUsers.Count != sourceUserIds.Count)
        {
            throw new InvalidOperationException("Один из выбранных пользователей не найден.");
        }

        var allIdentities = await _context.UserExternalIdentities
            .Where(x => allUserIds.Contains(x.UserId))
            .ToListAsync(cancellationToken);

        var conflictingProvider = allIdentities
            .GroupBy(x => x.Provider, StringComparer.Ordinal)
            .FirstOrDefault(group => group
                .Select(x => (x.ProviderUserId ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Count() > 1);

        if (conflictingProvider is not null)
        {
            throw new InvalidOperationException($"Нельзя объединить пользователей: у них конфликтуют привязки провайдера {conflictingProvider.Key}.");
        }

        var preferredEmail = ResolveMergedEmailCandidate(request.PrimaryEmail, targetUser, sourceUsers, allIdentities);
        if (!string.IsNullOrWhiteSpace(preferredEmail))
        {
            var normalizedPreferredEmail = NormalizeEmail(preferredEmail);
            var emailExists = await _context.Users
                .AsNoTracking()
                .AnyAsync(
                    x => !allUserIds.Contains(x.Id) && x.Email == normalizedPreferredEmail,
                    cancellationToken);
            if (emailExists)
            {
                throw new InvalidOperationException("Выбранный email уже используется другим пользователем.");
            }

            preferredEmail = normalizedPreferredEmail;
        }

        var preferredPhone = ResolveMergedPhoneCandidate(request.PrimaryPhoneNumber, targetUser, sourceUsers);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var sourceUser in sourceUsers)
            {
                MergeUserFields(targetUser, sourceUser);
                await ReassignSimpleUserReferencesAsync(sourceUser.Id, targetUser.Id, cancellationToken);
                await MergePaymentResponsiblesAsync(sourceUser.Id, targetUser.Id, cancellationToken);
                await MergeUserSettingsAsync(sourceUser.Id, targetUser.Id, cancellationToken);
                await MergeUserExternalIdentitiesAsync(sourceUser.Id, targetUser.Id, cancellationToken);

                if (targetUser.ApprovedByUserId == sourceUser.Id)
                {
                    targetUser.ApprovedByUserId = sourceUser.ApprovedByUserId == targetUser.Id
                        ? null
                        : sourceUser.ApprovedByUserId;
                }

                _context.Users.Remove(sourceUser);
                await _context.SaveChangesAsync(cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(preferredEmail))
            {
                targetUser.Email = preferredEmail;
            }

            if (!string.IsNullOrWhiteSpace(preferredPhone))
            {
                targetUser.PhoneNumber = preferredPhone;
            }

            if (string.IsNullOrWhiteSpace(targetUser.FullName))
            {
                targetUser.FullName = BuildFallbackUserFullName(targetUser);
            }

            targetUser.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        var mergedUser = await QueryUsersWithRelations()
            .FirstAsync(x => x.Id == targetUser.Id, cancellationToken);

        return MapToDto(mergedUser);
    }

    private static void MergeUserFields(User targetUser, User sourceUser)
    {
        if (string.IsNullOrWhiteSpace(targetUser.FullName) && !string.IsNullOrWhiteSpace(sourceUser.FullName))
        {
            targetUser.FullName = sourceUser.FullName;
        }

        if (string.IsNullOrWhiteSpace(targetUser.FirstName) && !string.IsNullOrWhiteSpace(sourceUser.FirstName))
        {
            targetUser.FirstName = sourceUser.FirstName;
        }

        if (string.IsNullOrWhiteSpace(targetUser.LastName) && !string.IsNullOrWhiteSpace(sourceUser.LastName))
        {
            targetUser.LastName = sourceUser.LastName;
        }

        if (string.IsNullOrWhiteSpace(targetUser.MiddleName) && !string.IsNullOrWhiteSpace(sourceUser.MiddleName))
        {
            targetUser.MiddleName = sourceUser.MiddleName;
        }

        if (!targetUser.DateOfBirth.HasValue && sourceUser.DateOfBirth.HasValue)
        {
            targetUser.DateOfBirth = sourceUser.DateOfBirth;
        }

        if (string.IsNullOrWhiteSpace(targetUser.PhotoUrl) && !string.IsNullOrWhiteSpace(sourceUser.PhotoUrl))
        {
            targetUser.PhotoUrl = sourceUser.PhotoUrl;
        }

        if (string.IsNullOrWhiteSpace(targetUser.PhoneNumber) && !string.IsNullOrWhiteSpace(sourceUser.PhoneNumber))
        {
            targetUser.PhoneNumber = sourceUser.PhoneNumber;
        }

        if (string.IsNullOrWhiteSpace(targetUser.WhatsApp) && !string.IsNullOrWhiteSpace(sourceUser.WhatsApp))
        {
            targetUser.WhatsApp = sourceUser.WhatsApp;
        }

        if (string.IsNullOrWhiteSpace(targetUser.Telegram) && !string.IsNullOrWhiteSpace(sourceUser.Telegram))
        {
            targetUser.Telegram = sourceUser.Telegram;
        }

        if (string.IsNullOrWhiteSpace(targetUser.Instagram) && !string.IsNullOrWhiteSpace(sourceUser.Instagram))
        {
            targetUser.Instagram = sourceUser.Instagram;
        }

        if (string.IsNullOrWhiteSpace(targetUser.Messenger) && !string.IsNullOrWhiteSpace(sourceUser.Messenger))
        {
            targetUser.Messenger = sourceUser.Messenger;
        }

        if (string.IsNullOrWhiteSpace(targetUser.Viber) && !string.IsNullOrWhiteSpace(sourceUser.Viber))
        {
            targetUser.Viber = sourceUser.Viber;
        }

        if (!targetUser.IsEmployee && sourceUser.IsEmployee)
        {
            targetUser.IsEmployee = true;
        }

        if (!targetUser.EmploymentStartDate.HasValue && sourceUser.EmploymentStartDate.HasValue)
        {
            targetUser.EmploymentStartDate = sourceUser.EmploymentStartDate;
        }

        if (!targetUser.EmploymentEndDate.HasValue && sourceUser.EmploymentEndDate.HasValue)
        {
            targetUser.EmploymentEndDate = sourceUser.EmploymentEndDate;
        }

        if (!targetUser.IsApproved && sourceUser.IsApproved)
        {
            targetUser.IsApproved = true;
            targetUser.ApprovedAt = sourceUser.ApprovedAt;
            targetUser.ApprovedByUserId = sourceUser.ApprovedByUserId == targetUser.Id
                ? null
                : sourceUser.ApprovedByUserId;
        }

        if (!targetUser.IsActive && sourceUser.IsActive)
        {
            targetUser.IsActive = true;
        }
    }

    private async Task ReassignSimpleUserReferencesAsync(int sourceUserId, int targetUserId, CancellationToken cancellationToken)
    {
        var clientAssignments = await _context.Clients
            .Where(x => x.ResponsibleUserId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var client in clientAssignments)
        {
            client.ResponsibleUserId = targetUserId;
        }

        var legalEntityAssignments = await _context.LegalEntities
            .Where(x => x.ResponsibleUserId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var legalEntity in legalEntityAssignments)
        {
            legalEntity.ResponsibleUserId = targetUserId;
        }

        var actAssignments = await _context.Acts
            .Where(x => x.ResponsibleId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var act in actAssignments)
        {
            act.ResponsibleId = targetUserId;
        }

        var authRequests = await _context.ExternalAuthRequests
            .Where(x => x.UserId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var authRequest in authRequests)
        {
            authRequest.UserId = targetUserId;
        }

        var telegramRequests = await _context.TelegramAuthRequests
            .Where(x => x.UserId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var telegramRequest in telegramRequests)
        {
            telegramRequest.UserId = targetUserId;
        }

        var activityLogs = await _context.UserActivityLogs
            .Where(x => x.UserId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var activityLog in activityLogs)
        {
            activityLog.UserId = targetUserId;
        }

        var approvedUsers = await _context.Users
            .Where(x => x.ApprovedByUserId == sourceUserId && x.Id != targetUserId)
            .ToListAsync(cancellationToken);
        foreach (var approvedUser in approvedUsers)
        {
            approvedUser.ApprovedByUserId = targetUserId;
        }

        var roleAudits = await _context.Roles
            .Where(x => x.PermissionsUpdatedByUserId == sourceUserId)
            .ToListAsync(cancellationToken);
        foreach (var role in roleAudits)
        {
            role.PermissionsUpdatedByUserId = targetUserId;
        }
    }

    private async Task MergePaymentResponsiblesAsync(int sourceUserId, int targetUserId, CancellationToken cancellationToken)
    {
        var targetPaymentIds = await _context.PaymentResponsibles
            .Where(x => x.UserId == targetUserId)
            .Select(x => x.PaymentId)
            .ToHashSetAsync(cancellationToken);

        var sourceResponsibles = await _context.PaymentResponsibles
            .Where(x => x.UserId == sourceUserId)
            .ToListAsync(cancellationToken);

        foreach (var responsible in sourceResponsibles)
        {
            if (targetPaymentIds.Contains(responsible.PaymentId))
            {
                _context.PaymentResponsibles.Remove(responsible);
                continue;
            }

            responsible.UserId = targetUserId;
            targetPaymentIds.Add(responsible.PaymentId);
        }
    }

    private async Task MergeUserSettingsAsync(int sourceUserId, int targetUserId, CancellationToken cancellationToken)
    {
        var targetKeys = await _context.UserSettings
            .Where(x => x.UserId == targetUserId)
            .Select(x => x.Key)
            .ToHashSetAsync(cancellationToken);

        var sourceSettings = await _context.UserSettings
            .Where(x => x.UserId == sourceUserId)
            .ToListAsync(cancellationToken);

        foreach (var setting in sourceSettings)
        {
            if (targetKeys.Contains(setting.Key))
            {
                _context.UserSettings.Remove(setting);
                continue;
            }

            setting.UserId = targetUserId;
            targetKeys.Add(setting.Key);
        }
    }

    private async Task MergeUserExternalIdentitiesAsync(int sourceUserId, int targetUserId, CancellationToken cancellationToken)
    {
        var targetIdentities = await _context.UserExternalIdentities
            .Where(x => x.UserId == targetUserId)
            .ToListAsync(cancellationToken);

        var sourceIdentities = await _context.UserExternalIdentities
            .Where(x => x.UserId == sourceUserId)
            .ToListAsync(cancellationToken);

        foreach (var identity in sourceIdentities)
        {
            var existingIdentity = targetIdentities.FirstOrDefault(x => x.Provider == identity.Provider);
            if (existingIdentity is not null)
            {
                if (!string.Equals(existingIdentity.ProviderUserId, identity.ProviderUserId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Конфликт привязки провайдера {identity.Provider}.");
                }

                _context.UserExternalIdentities.Remove(identity);
                continue;
            }

            identity.UserId = targetUserId;
            targetIdentities.Add(identity);
        }
    }

    private static string? ResolveMergedEmailCandidate(
        string? preferredEmail,
        User targetUser,
        IEnumerable<User> sourceUsers,
        IEnumerable<UserExternalIdentity> identities)
    {
        if (!string.IsNullOrWhiteSpace(preferredEmail))
        {
            var requestedEmail = TechnicalEmailHelper.NormalizeRealEmail(preferredEmail);
            if (string.IsNullOrWhiteSpace(requestedEmail))
            {
                throw new InvalidOperationException("Основным email может быть только валидный email.");
            }

            return requestedEmail;
        }

        var currentEmail = TechnicalEmailHelper.NormalizeRealEmail(targetUser.Email);
        if (!string.IsNullOrWhiteSpace(currentEmail))
        {
            return currentEmail;
        }

        foreach (var email in sourceUsers.Select(x => TechnicalEmailHelper.NormalizeRealEmail(x.Email)))
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                return email;
            }
        }

        foreach (var email in identities.Select(x => TechnicalEmailHelper.NormalizeRealEmail(x.ProviderEmail)))
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                return email;
            }
        }

        return null;
    }

    private static string? ResolveMergedPhoneCandidate(
        string? preferredPhone,
        User targetUser,
        IEnumerable<User> sourceUsers)
    {
        var requestedPhone = NormalizeOptionalValue(preferredPhone);
        if (!string.IsNullOrWhiteSpace(requestedPhone))
        {
            return requestedPhone;
        }

        var currentPhone = NormalizeOptionalValue(targetUser.PhoneNumber);
        if (!string.IsNullOrWhiteSpace(currentPhone))
        {
            return currentPhone;
        }

        foreach (var phone in sourceUsers.Select(x => NormalizeOptionalValue(x.PhoneNumber)))
        {
            if (!string.IsNullOrWhiteSpace(phone))
            {
                return phone;
            }
        }

        return null;
    }
}
