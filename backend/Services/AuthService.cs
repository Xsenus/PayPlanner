using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace PayPlanner.Api.Services;

public partial class AuthService
{
    private readonly IConfiguration _configuration;
    private readonly PaymentContext _context;
    private readonly IEmailService _emailService;
    private readonly IPasswordHasher<User> _passwordHasher;

    public AuthService(
        PaymentContext context,
        IConfiguration configuration,
        IEmailService emailService,
        IPasswordHasher<User> passwordHasher)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _passwordHasher = passwordHasher;
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

    private static UserDto MapToDto(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FullName = user.FullName,
        FirstName = user.FirstName,
        LastName = user.LastName,
        MiddleName = user.MiddleName,
        DateOfBirth = user.DateOfBirth,
        PhotoUrl = user.PhotoUrl,
        PhoneNumber = user.PhoneNumber,
        WhatsApp = user.WhatsApp,
        Telegram = user.Telegram,
        Instagram = user.Instagram,
        Messenger = user.Messenger,
        Viber = user.Viber,
        IsEmployee = user.IsEmployee,
        EmploymentStartDate = user.EmploymentStartDate,
        EmploymentEndDate = user.EmploymentEndDate,
        IsActive = user.IsActive,
        IsApproved = user.IsApproved,
        ApprovedAt = user.ApprovedAt,
        CreatedAt = user.CreatedAt,
        ExternalIdentities = MapExternalIdentities(user.ExternalIdentities),
        Role = new RoleDto
        {
            Id = user.Role?.Id ?? 0,
            Name = user.Role?.Name ?? "user",
            Description = user.Role?.Description ?? string.Empty,
            CreatedAt = user.Role?.CreatedAt,
            IsSystemRole = user.Role?.Name == "admin",
            TotalSectionsCount = RolePermissionCatalog.AllSections.Length
        }
    };

    public static string NormalizeEmailValue(string email)
        => NormalizeEmail(email);

    public UserDto MapUserToDto(User user)
        => MapToDto(user);

    public async Task<LoginResponse> BuildLoginResponseAsync(User user, CancellationToken cancellationToken = default)
    {
        if (user.Role is null)
        {
            await _context.Entry(user).Reference(x => x.Role).LoadAsync(cancellationToken);
        }

        await _context.Entry(user).Collection(x => x.ExternalIdentities).LoadAsync(cancellationToken);

        return new LoginResponse
        {
            Token = GenerateJwtToken(user),
            User = MapToDto(user)
        };
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

        user.IsApproved = true;
        user.ApprovedAt = DateTime.UtcNow;
        user.ApprovedByUserId = approvedByUserId;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await _emailService.SendRegistrationApprovedEmailAsync(user.Email, user.FullName);
        return true;
    }

    public async Task<bool> RejectUserAsync(int userId, string? reason = null)
    {
        var user = await _context.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return false;
        }

        await _emailService.SendRegistrationRejectedEmailAsync(user.Email, user.FullName, reason);
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

        var roleExists = await _context.Roles
            .AsNoTracking()
            .AnyAsync(r => r.Id == request.RoleId);
        if (!roleExists)
        {
            throw new InvalidOperationException("RoleNotFound");
        }

        var user = new User
        {
            Email = email,
            FullName = (request.FullName ?? string.Empty).Trim(),
            RoleId = request.RoleId,
            IsActive = request.IsActive,
            IsApproved = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        await _context.Entry(user).Reference(u => u.Role).LoadAsync();

        return MapToDto(user);
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

        var query = ApplyUserStatusFilter(QueryUsersWithRelations().AsNoTracking(), status);
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

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request)
    {
        var user = await QueryUsersWithRelations()
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            return null;
        }

        var previousRoleId = user.RoleId;
        await ApplyUserChangesAsync(user, request, allowAdministrativeFields: true);
        await _context.SaveChangesAsync();

        if (previousRoleId != user.RoleId)
        {
            await _context.Entry(user).Reference(u => u.Role).LoadAsync();
        }

        return MapToDto(user);
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return false;
        }

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

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<UserDto>> GetAllUsersAsync(string? status = null)
    {
        var query = ApplyUserStatusFilter(QueryUsersWithRelations().AsNoTracking(), status);
        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return users.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetUserByIdAsync(int id)
    {
        var user = await QueryUsersWithRelations()
            .AsNoTracking()
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

        var defaultRoleId = await GetDefaultUserRoleIdAsync();

        var user = new User
        {
            Email = email,
            FullName = request.FullName,
            RoleId = defaultRoleId,
            IsActive = true,
            IsApproved = false,
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
