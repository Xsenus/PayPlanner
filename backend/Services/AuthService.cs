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

public class AuthService
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

    private string GenerateJwtToken(User user)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_configuration["Jwt:Secret"] ?? "your-secret-key-min-32-characters-long!")
        );

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
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static UserDto MapToDto(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FullName = user.FullName,

        // профиль
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

        // статусы/даты
        IsActive = user.IsActive,
        IsApproved = user.IsApproved,
        ApprovedAt = user.ApprovedAt,
        CreatedAt = user.CreatedAt,

        // роль
        Role = new RoleDto
        {
            Id = user.Role?.Id ?? 0,
            Name = user.Role?.Name ?? "user",
            Description = user.Role?.Description ?? string.Empty
        }
    };


    // -------- Admin moderation --------

    public async Task<bool> ApproveUserAsync(int userId, int approvedByUserId)
    {
        var approverExists = await _context.Users.AsNoTracking()
            .AnyAsync(u => u.Id == approvedByUserId);
        if (!approverExists) return false;

        var user = await _context.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.IsApproved) return false;

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
        if (user == null) return false;

        await _emailService.SendRegistrationRejectedEmailAsync(user.Email, user.FullName, reason);
        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    // -------- CRUD --------

    public async Task<UserDto?> CreateUserAsync(CreateUserRequest request)
    {
        var email = NormalizeEmail(request.Email);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
            throw new ArgumentException("Email and Password are required.");

        // Не создаём дубликаты
        var emailExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Email == email);
        if (emailExists)
            return null;

        // Проверяем валидность роли
        var roleExists = await _context.Roles
            .AsNoTracking()
            .AnyAsync(r => r.Id == request.RoleId);
        if (!roleExists)
            throw new InvalidOperationException("RoleNotFound");

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
        var roles = await _context.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description
            })
            .ToListAsync();

        return roles;
    }


    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null)
            return null;

        // ---- strings: если поле пришло (!= null) — применяем (с trim). Пустая строка допустима для "очистки".
        if (request.FullName != null) user.FullName = request.FullName.Trim();
        if (request.FirstName != null) user.FirstName = request.FirstName.Trim();
        if (request.LastName != null) user.LastName = request.LastName.Trim();
        if (request.MiddleName != null) user.MiddleName = request.MiddleName.Trim();
        if (request.PhotoUrl != null) user.PhotoUrl = request.PhotoUrl.Trim();
        if (request.PhoneNumber != null) user.PhoneNumber = request.PhoneNumber.Trim();
        if (request.WhatsApp != null) user.WhatsApp = request.WhatsApp.Trim();
        if (request.Telegram != null) user.Telegram = request.Telegram.Trim();
        if (request.Instagram != null) user.Instagram = request.Instagram.Trim();
        if (request.Messenger != null) user.Messenger = request.Messenger.Trim();
        if (request.Viber != null) user.Viber = request.Viber.Trim();

        // ---- dates/bools: применяем, если HasValue
        if (request.DateOfBirth.HasValue)
        {
            if (request.DateOfBirth > DateTime.UtcNow.Date)
                throw new ArgumentException("DateOfBirth cannot be in the future.");
            user.DateOfBirth = request.DateOfBirth;
        }

        if (request.EmploymentStartDate.HasValue)
            user.EmploymentStartDate = request.EmploymentStartDate;

        if (request.EmploymentEndDate.HasValue)
            user.EmploymentEndDate = request.EmploymentEndDate;

        if (user.EmploymentStartDate.HasValue && user.EmploymentEndDate.HasValue &&
            user.EmploymentStartDate > user.EmploymentEndDate)
            throw new ArgumentException("EmploymentStartDate cannot be after EmploymentEndDate.");

        if (request.IsEmployee.HasValue)
            user.IsEmployee = request.IsEmployee.Value;

        var roleChanged = false;
        if (request.RoleId.HasValue && request.RoleId.Value != user.RoleId)
        {
            user.RoleId = request.RoleId.Value;
            roleChanged = true;
        }

        if (request.IsActive.HasValue)
            user.IsActive = request.IsActive.Value;

        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        if (roleChanged)
            await _context.Entry(user).Reference(u => u.Role).LoadAsync();

        return MapToDto(user);
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return false;

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<UserDto>> GetAllUsersAsync(string? status = null)
    {
        var q = _context.Users.Include(u => u.Role).AsQueryable();

        // Дополнен фильтр "inactive" (из второго варианта), остальное — как было.
        if (status == "pending") q = q.Where(u => !u.IsApproved);
        else if (status == "approved") q = q.Where(u => u.IsApproved);
        else if (status == "inactive") q = q.Where(u => !u.IsActive);

        var users = await q.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return users.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetUserByIdAsync(int id)
    {
        var user = await _context.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id);
        return user == null ? null : MapToDto(user);
    }

    // -------- Auth --------

    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        var emailNorm = NormalizeEmail(email);

        var user = await _context.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Email == emailNorm);

        // Не раскрываем существование пользователя — вернём null (401 на фронте).
        if (user == null) return null;

        if (!user.IsActive)
            throw new InvalidOperationException("UserInactive");

        if (!user.IsApproved)
            throw new InvalidOperationException("PendingApproval");

        var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed) return null;

        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        var token = GenerateJwtToken(user);

        return new LoginResponse
        {
            Token = token,
            User = MapToDto(user)
        };
    }

    public async Task<UserDto?> RegisterAsync(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);

        if (await _context.Users.AnyAsync(u => u.Email == email))
            return null;

        var registrationEnabled = _configuration.GetValue<bool>("Features:RegistrationEnabled", true);
        if (!registrationEnabled)
            throw new InvalidOperationException("Registration is currently disabled");

        var defaultRoleId = await _context.Roles
            .Where(r => r.Name == "user")
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        if (defaultRoleId == 0)
            throw new InvalidOperationException("Default user role not found");

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

        user = await _context.Users.Include(u => u.Role)
            .FirstAsync(u => u.Id == user.Id);

        await _emailService.SendRegistrationPendingEmailAsync(user.Email, user.FullName);

        var notifyAdmins = _configuration.GetValue<bool>("Features:NotifyAdminsOnRegistration", false);
        if (notifyAdmins)
        {
            var adminEmails = await _context.Users
                .Include(u => u.Role)
                .Where(u => u.Role!.Name == "admin" && u.IsActive && u.IsApproved)
                .Select(u => u.Email)
                .ToListAsync();

            foreach (var adminEmail in adminEmails)
            {
                await _emailService.SendAdminNewRegistrationNotificationAsync(
                    adminEmail, user.FullName, user.Email);
            }
        }

        return MapToDto(user);
    }
}
