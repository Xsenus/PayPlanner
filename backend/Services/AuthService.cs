using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace PayPlanner.Api.Services;

public partial class AuthService
{
    private readonly IConfiguration _configuration;
    private readonly PaymentContext _context;
    private readonly IEmailService _emailService;
    private readonly IWebHostEnvironment _environment;
    private readonly IExternalCalendarSyncCoordinator? _externalCalendarSyncCoordinator;
    private readonly INotificationService? _notificationService;
    private readonly IPasswordHasher<User> _passwordHasher;
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";

    public AuthService(
        PaymentContext context,
        IConfiguration configuration,
        IEmailService emailService,
        IWebHostEnvironment environment,
        IPasswordHasher<User> passwordHasher,
        INotificationService? notificationService = null,
        IExternalCalendarSyncCoordinator? externalCalendarSyncCoordinator = null)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _environment = environment;
        _passwordHasher = passwordHasher;
        _notificationService = notificationService;
        _externalCalendarSyncCoordinator = externalCalendarSyncCoordinator;
    }

    private static string NormalizeEmail(string email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static IQueryable<User> ApplyUserStatusFilter(IQueryable<User> query, string? status)
    {
        return (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pending" => query.Where(u => !u.IsApproved),
            "approved" => query.Where(u => u.IsApproved),
            "inactive" => query.Where(u => !u.IsActive),
            _ => query
        };
    }

    private string GenerateJwtToken(User user)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_configuration["Jwt:Secret"] ?? "your-secret-key-min-32-characters-long!"));

        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role?.Name ?? "user")
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "PayPlanner",
            audience: _configuration["Jwt:Audience"] ?? "PayPlanner",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string? ResolvePhotoUrl(string? photoUrl)
    {
        if (string.IsNullOrWhiteSpace(photoUrl))
        {
            return null;
        }

        var photoPath = photoUrl.Trim();
        if (Uri.TryCreate(photoPath, UriKind.Absolute, out var absoluteUri))
        {
            photoPath = absoluteUri.AbsolutePath;
        }

        if (!photoPath.StartsWith("/user-photos/", StringComparison.OrdinalIgnoreCase))
        {
            return photoUrl;
        }

        if (string.IsNullOrWhiteSpace(_environment.WebRootPath))
        {
            return photoUrl;
        }

        var relativePath = photoPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var rootPath = Path.GetFullPath(_environment.WebRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));

        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? photoUrl : null;
    }

    private sealed record AuditMetadataDto(
        int? CreatedByUserId,
        string? CreatedByUserName,
        DateTime? UpdatedAt,
        int? UpdatedByUserId,
        string? UpdatedByUserName);

    private static AuditMetadataDto ReadAuditMetadata(EntityEntry entry)
        => new(
            ReadNullable<int>(entry, "CreatedByUserId"),
            ReadNullable<string>(entry, "CreatedByUserName"),
            ReadNullable<DateTime>(entry, "UpdatedAt"),
            ReadNullable<int>(entry, "UpdatedByUserId"),
            ReadNullable<string>(entry, "UpdatedByUserName"));

    private static T? ReadNullable<T>(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null)
        {
            return default;
        }

        var value = entry.Property(propertyName).CurrentValue;
        return value is T typed ? typed : default;
    }

    private UserDto MapToDto(User user)
    {
        var userAudit = ReadAuditMetadata(_context.Entry(user));
        var roleAudit = user.Role is not null
            ? ReadAuditMetadata(_context.Entry(user.Role))
            : null;
        var companyAccesses = (user.CompanyAccesses ?? Array.Empty<UserCompanyAccess>())
            .Where(access => access.LegalEntity is not null)
            .OrderBy(access => access.LegalEntity.ShortName)
            .ThenBy(access => access.LegalEntity.FullName)
            .ToList();

        return new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            FirstName = user.FirstName,
            LastName = user.LastName,
            MiddleName = user.MiddleName,
            DateOfBirth = user.DateOfBirth,
            PhotoUrl = ResolvePhotoUrl(user.PhotoUrl),
            PhoneNumber = user.PhoneNumber,
            WhatsApp = user.WhatsApp,
            Telegram = user.Telegram,
            Instagram = user.Instagram,
            Messenger = user.Messenger,
            Viber = user.Viber,
            IsEmployee = user.IsEmployee,
            IsExternalContractor = user.IsExternalContractor,
            DepartmentId = user.DepartmentId,
            Department = user.DepartmentDictionary?.Name ?? user.Department,
            RequiresFinancialActionReason = user.RequiresFinancialActionReason,
            EmploymentStartDate = user.EmploymentStartDate,
            EmploymentEndDate = user.EmploymentEndDate,
            IsActive = user.IsActive,
            IsApproved = user.IsApproved,
            IsProtectedUser = IsProtectedSystemUser(user),
            ApprovedAt = user.ApprovedAt,
            LastActivityAt = user.LastActivityAt,
            LastLoginAt = user.LastLoginAt,
            LastLogoutAt = user.LastLogoutAt,
            CreatedAt = user.CreatedAt,
            CreatedByUserId = userAudit.CreatedByUserId,
            CreatedByUserName = userAudit.CreatedByUserName,
            UpdatedAt = userAudit.UpdatedAt,
            UpdatedByUserId = userAudit.UpdatedByUserId,
            UpdatedByUserName = userAudit.UpdatedByUserName,
            CompanyIds = companyAccesses
                .Select(access => access.LegalEntityId)
                .Distinct()
                .OrderBy(id => id)
                .ToList(),
            Companies = companyAccesses
                .Select(access => new UserCompanyAccessDto
                {
                    Id = access.LegalEntityId,
                    ShortName = access.LegalEntity.ShortName,
                    FullName = access.LegalEntity.FullName,
                    Inn = access.LegalEntity.Inn,
                    Kpp = access.LegalEntity.Kpp,
                })
                .ToList(),
            ExternalIdentities = MapExternalIdentities(user.ExternalIdentities),
            Role = new RoleDto
            {
                Id = user.Role?.Id ?? 0,
                Name = user.Role?.Name ?? "user",
                Description = user.Role?.Description ?? string.Empty,
                CreatedAt = user.Role?.CreatedAt,
                CreatedByUserId = roleAudit?.CreatedByUserId,
                CreatedByUserName = roleAudit?.CreatedByUserName,
                UpdatedAt = roleAudit?.UpdatedAt,
                UpdatedByUserId = roleAudit?.UpdatedByUserId,
                UpdatedByUserName = roleAudit?.UpdatedByUserName,
                IsSystemRole = user.Role?.Name == "admin",
                TotalSectionsCount = RolePermissionCatalog.AllSections.Length
            }
        };
    }

    public static string NormalizeEmailValue(string email)
        => NormalizeEmail(email);

    public UserDto MapUserToDto(User user)
        => MapToDto(user);

    public async Task<LoginResponse> BuildLoginResponseAsync(User user, CancellationToken cancellationToken = default)
    {
        await MarkUserLoginAsync(user, cancellationToken);

        if (user.Role is null)
        {
            await _context.Entry(user).Reference(x => x.Role).LoadAsync(cancellationToken);
        }

        await _context.Entry(user).Collection(x => x.ExternalIdentities).LoadAsync(cancellationToken);
        await _context.Entry(user)
            .Collection(x => x.CompanyAccesses)
            .Query()
            .Include(x => x.LegalEntity)
            .LoadAsync(cancellationToken);

        return new LoginResponse
        {
            Token = GenerateJwtToken(user),
            User = MapToDto(user)
        };
    }

    private async Task MarkUserLoginAsync(User user, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await _context.Users
            .Where(x => x.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastLoginAt, now)
                .SetProperty(x => x.LastActivityAt, now)
                .SetProperty(x => x.LastActivityAction, (string?)"login")
                .SetProperty(x => x.LastActivitySection, (string?)"auth"),
                cancellationToken);

        user.LastLoginAt = now;
        user.LastActivityAt = now;
        user.LastActivityAction = "login";
        user.LastActivitySection = "auth";
    }

    public async Task<int> GetDefaultUserRoleIdAsync(CancellationToken cancellationToken = default)
    {
        var defaultRoleId = await _context.Roles
            .Where(r => r.Name == "user")
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (defaultRoleId == 0)
        {
            throw new InvalidOperationException("Default user role not found");
        }

        return defaultRoleId;
    }

    public async Task NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default)
    {
        if (_notificationService is not null)
        {
            await _notificationService.NotifyPendingRegistrationAsync(user, cancellationToken);
            return;
        }

        if (user.Role is null)
        {
            await _context.Entry(user).Reference(x => x.Role).LoadAsync(cancellationToken);
        }

        if (TechnicalEmailHelper.IsValidRealEmail(user.Email))
        {
            await _emailService.SendRegistrationPendingEmailAsync(user.Email, user.FullName);
        }

        var notifyAdmins = _configuration.GetValue<bool>("Features:NotifyAdminsOnRegistration", false);
        if (!notifyAdmins)
        {
            return;
        }

        var adminEmails = await _context.Users
            .Include(u => u.Role)
            .Where(u => u.Role!.Name == "admin" && u.IsActive && u.IsApproved)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken);

        foreach (var adminEmail in adminEmails)
        {
            await _emailService.SendAdminNewRegistrationNotificationAsync(
                adminEmail,
                user.FullName,
                user.Email);
        }
    }

    public static string CreateGeneratedPassword()
        => $"{Guid.NewGuid():N}!Aa9";

    public async Task<bool> ApproveUserAsync(int userId, int approvedByUserId)
    {
        var approverExists = await _context.Users.AsNoTracking()
            .AnyAsync(u => u.Id == approvedByUserId);
        if (!approverExists)
        {
            return false;
        }

        var user = await _context.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.IsApproved)
        {
            return false;
        }

        EnsureProtectedUserManagedBySelf(user, approvedByUserId);

        user.IsApproved = true;
        user.ApprovedAt = DateTime.UtcNow;
        user.ApprovedByUserId = approvedByUserId;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        if (_notificationService is not null)
        {
            await _notificationService.NotifyRegistrationApprovedAsync(user);
        }
        else
        {
            await _emailService.SendRegistrationApprovedEmailAsync(user.Email, user.FullName);
        }
        return true;
    }

    public async Task<bool> RejectUserAsync(int userId, int actorUserId, string? reason = null)
    {
        var user = await _context.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return false;
        }

        EnsureProtectedUserManagedBySelf(user, actorUserId);
        EnsureProtectedUserNotDeleted(user);

        if (_notificationService is not null)
        {
            await _notificationService.NotifyRegistrationRejectedAsync(user, reason);
        }
        else
        {
            await _emailService.SendRegistrationRejectedEmailAsync(user.Email, user.FullName, reason);
        }

        if (_externalCalendarSyncCoordinator is not null)
        {
            await _externalCalendarSyncCoordinator.CleanupDeletedUserAsync(userId, actorUserId);
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<UserDto?> CreateUserAsync(CreateUserRequest request)
    {
        var email = NormalizeEmail(request.Email);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("Email and Password are required.");
        }

        var emailExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Email == email);
        if (emailExists)
        {
            return null;
        }

        var requestedRoleId = request.RoleId;
        if (IsProtectedSystemUserEmail(email))
        {
            requestedRoleId = await GetAdministratorRoleIdAsync();
        }

        var roleExists = await _context.Roles
            .AsNoTracking()
            .AnyAsync(r => r.Id == requestedRoleId);
        if (!roleExists)
        {
            throw new InvalidOperationException("RoleNotFound");
        }

        var departmentAssignment = await ResolveDepartmentAssignmentAsync(
            request.DepartmentId,
            request.Department,
            CancellationToken.None);

        var user = new User
        {
            Email = email,
            FullName = (request.FullName ?? string.Empty).Trim(),
            RoleId = requestedRoleId,
            IsEmployee = request.IsEmployee,
            IsExternalContractor = request.IsExternalContractor,
            DepartmentId = departmentAssignment.DepartmentId,
            Department = departmentAssignment.DepartmentName,
            RequiresFinancialActionReason = request.RequiresFinancialActionReason,
            IsActive = IsProtectedSystemUserEmail(email) || request.IsActive,
            IsApproved = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        await using var transaction = await _context.Database.BeginTransactionAsync();
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        await ApplyUserCompanyAssignmentsAsync(user, request.CompanyIds);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        var createdUser = await QueryUsersWithRelations()
            .FirstAsync(u => u.Id == user.Id);

        return MapToDto(createdUser);
    }

    public async Task<List<RoleDto>> GetAllRolesAsync()
    {
        return await _context.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                CreatedAt = r.CreatedAt,
                CreatedByUserId = EF.Property<int?>(r, "CreatedByUserId"),
                CreatedByUserName = EF.Property<string?>(r, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(r, "UpdatedAt"),
                UpdatedByUserId = EF.Property<int?>(r, "UpdatedByUserId"),
                UpdatedByUserName = EF.Property<string?>(r, "UpdatedByUserName"),
                IsSystemRole = r.Name == "admin",
                AssignedUsersCount = r.Users.Count(),
                AccessibleSectionsCount = r.Permissions.Count(permission => permission.CanView),
                TotalSectionsCount = RolePermissionCatalog.AllSections.Length,
                PermissionsUpdatedAt = r.PermissionsUpdatedAt,
                PermissionsUpdatedByUserId = r.PermissionsUpdatedByUserId,
                PermissionsUpdatedByUserName = r.PermissionsUpdatedByUserName
            })
            .ToListAsync();
    }

    public async Task<PagedResponse<UserDto>> GetUsersPagedAsync(string? status = null, int page = 1, int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = ApplyUserStatusFilter(QueryUsersWithRelations(), status);
        var total = await query.CountAsync();
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResponse<UserDto>
        {
            Items = users.Select(MapToDto).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request, int actorUserId)
    {
        var user = await QueryUsersWithRelations()
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            return null;
        }

        EnsureProtectedUserManagedBySelf(user, actorUserId);

        var previousRoleId = user.RoleId;
        await ApplyUserChangesAsync(user, request, allowAdministrativeFields: true);
        await _context.SaveChangesAsync();

        var refreshedUser = await QueryUsersWithRelations()
            .FirstAsync(u => u.Id == user.Id);

        return MapToDto(refreshedUser);
    }

    public async Task<bool> DeleteUserAsync(int id, int actorUserId)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return false;
        }

        EnsureProtectedUserManagedBySelf(user, actorUserId);
        EnsureProtectedUserNotDeleted(user);

        var hasActUsage = await _context.Acts.AsNoTracking().AnyAsync(a => a.ResponsibleId == id);
        if (hasActUsage)
        {
            throw new InvalidOperationException("UserInUseAsResponsible");
        }

        var hasInvoiceUsage = await _context.PaymentResponsibles.AsNoTracking().AnyAsync(pr => pr.UserId == id);
        if (hasInvoiceUsage)
        {
            throw new InvalidOperationException("UserInUseAsResponsible");
        }

        if (_externalCalendarSyncCoordinator is not null)
        {
            await _externalCalendarSyncCoordinator.CleanupDeletedUserAsync(id, actorUserId);
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<UserDto?> ChangePasswordAsync(
        int id,
        string? currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await QueryUsersWithRelations()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var normalizedNewPassword = (newPassword ?? string.Empty).Trim();
        if (normalizedNewPassword.Length < 8)
        {
            throw new ArgumentException("Новый пароль должен содержать не меньше 8 символов.");
        }

        var requiresCurrentPassword = !user.ExternalIdentities.Any();
        var normalizedCurrentPassword = currentPassword ?? string.Empty;

        if (requiresCurrentPassword)
        {
            if (string.IsNullOrWhiteSpace(normalizedCurrentPassword))
            {
                throw new InvalidOperationException("CurrentPasswordRequired");
            }

            var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, normalizedCurrentPassword);
            if (verify == PasswordVerificationResult.Failed)
            {
                throw new InvalidOperationException("InvalidCurrentPassword");
            }
        }
        else if (!string.IsNullOrWhiteSpace(normalizedCurrentPassword))
        {
            var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, normalizedCurrentPassword);
            if (verify == PasswordVerificationResult.Failed)
            {
                throw new InvalidOperationException("InvalidCurrentPassword");
            }
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, normalizedNewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(user);
    }

    public async Task<List<UserDto>> GetAllUsersAsync(string? status = null)
    {
        var query = ApplyUserStatusFilter(QueryUsersWithRelations(), status);
        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return users.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetUserByIdAsync(int id)
    {
        var user = await QueryUsersWithRelations()
            .FirstOrDefaultAsync(u => u.Id == id);
        return user == null ? null : MapToDto(user);
    }

    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        var emailNorm = NormalizeEmail(email);
        var user = await QueryUsersWithRelations()
            .FirstOrDefaultAsync(u => u.Email == emailNorm);
        if (user == null)
        {
            return null;
        }

        if (!user.IsActive)
        {
            throw new InvalidOperationException("UserInactive");
        }

        if (!user.IsApproved)
        {
            throw new InvalidOperationException("PendingApproval");
        }

        var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        await MarkUserLoginAsync(user, CancellationToken.None);

        return new LoginResponse
        {
            Token = GenerateJwtToken(user),
            User = MapToDto(user)
        };
    }

    public async Task<UserDto?> RegisterAsync(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);

        if (await _context.Users.AnyAsync(u => u.Email == email))
        {
            return null;
        }

        var registrationEnabled = _configuration.GetValue<bool>("Features:RegistrationEnabled", true);
        if (!registrationEnabled)
        {
            throw new InvalidOperationException("Registration is currently disabled");
        }

        if (IsProtectedSystemUserEmail(email))
        {
            throw new InvalidOperationException("ProtectedUserEmailReserved");
        }

        var defaultRoleId = await GetDefaultUserRoleIdAsync();
        var roleId = defaultRoleId;

        var user = new User
        {
            Email = email,
            FullName = request.FullName,
            RoleId = roleId,
            IsActive = true,
            IsApproved = false,
            ApprovedAt = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        user = await QueryUsersWithRelations()
            .FirstAsync(u => u.Id == user.Id);

        await NotifyPendingRegistrationAsync(user);
        return MapToDto(user);
    }
}
