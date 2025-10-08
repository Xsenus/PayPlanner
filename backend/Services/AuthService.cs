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

    public AuthService(PaymentContext context, IConfiguration configuration, IEmailService emailService,
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
        IsActive = user.IsActive,
        IsApproved = user.IsApproved,
        ApprovedAt = user.ApprovedAt,
        CreatedAt = user.CreatedAt,
        Role = new RoleDto
        {
            Id = user.Role?.Id ?? 0,
            Name = user.Role?.Name ?? "user",
            Description = user.Role?.Description ?? string.Empty
        }
    };

    public async Task<bool> ApproveUserAsync(int userId, int approvedByUserId)
    {
        var approverExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == approvedByUserId);

        if (!approverExists)
            return false;

        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null || user.IsApproved)
            return false;

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
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            return false;

        await _emailService.SendRegistrationRejectedEmailAsync(user.Email, user.FullName, reason);

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<UserDto?> CreateUserAsync(CreateUserRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (await _context.Users.AnyAsync(u => u.Email == email))
            return null;

        var user = new User
        {
            Email = email,
            FullName = request.FullName,
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

        user = await _context.Users.Include(u => u.Role).FirstAsync(u => u.Id == user.Id);
        return MapToDto(user);
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null)
            return null;

        user.FullName = request.FullName;
        user.RoleId = request.RoleId;
        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return MapToDto(user);
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return false;

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<UserDto>> GetAllUsersAsync(string? status = null)
    {
        var q = _context.Users.Include(u => u.Role).AsQueryable();

        if (status == "pending") q = q.Where(u => !u.IsApproved);
        else if (status == "approved") q = q.Where(u => u.IsApproved);

        var users = await q.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return users.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetUserByIdAsync(int id)
    {
        var user = await _context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id);
        return user == null ? null : MapToDto(user);
    }

    public async Task<List<RoleDto>> GetAllRolesAsync()
    {
        var roles = await _context.Roles.OrderBy(r => r.Name).ToListAsync();
        return roles.Select(r => new RoleDto
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description
        }).ToList();
    }

    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        var emailNorm = NormalizeEmail(email);

        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Email == emailNorm);

        // Не раскрываем, существует ли пользователь — 401
        if (user == null)
            return null;

        // Пользователь выключен — код для фронта
        if (!user.IsActive)
            throw new InvalidOperationException("UserInactive");

        // Не одобрен — код для фронта
        if (!user.IsApproved)
            throw new InvalidOperationException("PendingApproval");

        var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed)
            return null;

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

        user = await _context.Users.Include(u => u.Role).FirstAsync(u => u.Id == user.Id);

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
