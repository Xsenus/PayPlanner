using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public class AuthService
{
    private readonly PaymentContext _context;

    public AuthService(PaymentContext context)
    {
        _context = context;
    }

    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || !VerifyPassword(password, user.PasswordHash))
            return null;

        if (!user.IsActive)
            return null;

        if (!user.IsApproved)
            return new LoginResponse { RequiresApproval = true };

        return new LoginResponse
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = new RoleDto { Id = user.Role!.Id, Name = user.Role.Name, Description = user.Role.Description },
            IsActive = user.IsActive,
            RequiresApproval = false
        };
    }

    public async Task<UserDto?> RegisterAsync(RegisterRequest request)
    {
        var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (existingUser != null)
            return null;

        var defaultRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "user");
        if (defaultRole == null)
            return null;

        var user = new User
        {
            Email = request.Email,
            PasswordHash = HashPassword(request.Password),
            FullName = request.FullName,
            RoleId = defaultRole.Id,
            IsActive = true,
            IsApproved = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        await _context.Entry(user).Reference(u => u.Role).LoadAsync();

        return MapToDto(user);
    }

    public async Task<List<UserDto>> GetAllUsersAsync(string? status)
    {
        var query = _context.Users.Include(u => u.Role).AsQueryable();

        if (!string.IsNullOrEmpty(status))
        {
            query = status.ToLower() switch
            {
                "pending" => query.Where(u => !u.IsApproved && u.IsActive),
                "approved" => query.Where(u => u.IsApproved && u.IsActive),
                "inactive" => query.Where(u => !u.IsActive),
                _ => query
            };
        }

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return users.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetUserByIdAsync(int id)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        return user == null ? null : MapToDto(user);
    }

    public async Task<bool> ApproveUserAsync(int userId, int approverId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return false;

        user.IsApproved = true;
        user.ApprovedAt = DateTime.UtcNow;
        user.ApprovedByUserId = approverId;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RejectUserAsync(int userId, string? reason)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return false;

        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<UserDto?> CreateUserAsync(CreateUserRequest request)
    {
        var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (existingUser != null)
            return null;

        var user = new User
        {
            Email = request.Email,
            PasswordHash = HashPassword(request.Password),
            FullName = request.FullName,
            RoleId = request.RoleId,
            IsActive = request.IsActive,
            IsApproved = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        await _context.Entry(user).Reference(u => u.Role).LoadAsync();

        return MapToDto(user);
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null)
            return null;

        if (request.FullName != null) user.FullName = request.FullName;
        if (request.FirstName != null) user.FirstName = request.FirstName;
        if (request.LastName != null) user.LastName = request.LastName;
        if (request.MiddleName != null) user.MiddleName = request.MiddleName;
        if (request.DateOfBirth.HasValue) user.DateOfBirth = request.DateOfBirth;
        if (request.PhotoUrl != null) user.PhotoUrl = request.PhotoUrl;
        if (request.PhoneNumber != null) user.PhoneNumber = request.PhoneNumber;
        if (request.WhatsApp != null) user.WhatsApp = request.WhatsApp;
        if (request.Telegram != null) user.Telegram = request.Telegram;
        if (request.Instagram != null) user.Instagram = request.Instagram;
        if (request.Messenger != null) user.Messenger = request.Messenger;
        if (request.Viber != null) user.Viber = request.Viber;
        if (request.IsEmployee.HasValue) user.IsEmployee = request.IsEmployee.Value;
        if (request.EmploymentStartDate.HasValue) user.EmploymentStartDate = request.EmploymentStartDate;
        if (request.EmploymentEndDate.HasValue) user.EmploymentEndDate = request.EmploymentEndDate;
        if (request.RoleId.HasValue) user.RoleId = request.RoleId.Value;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;
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

    public async Task<List<RoleDto>> GetAllRolesAsync()
    {
        var roles = await _context.Roles.OrderBy(r => r.Name).ToListAsync();
        return roles.Select(r => new RoleDto { Id = r.Id, Name = r.Name, Description = r.Description }).ToList();
    }

    private UserDto MapToDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            FirstName = user.FirstName,
            LastName = user.LastName,
            MiddleName = user.MiddleName,
            DateOfBirth = user.DateOfBirth?.ToString("yyyy-MM-dd"),
            PhotoUrl = user.PhotoUrl,
            PhoneNumber = user.PhoneNumber,
            WhatsApp = user.WhatsApp,
            Telegram = user.Telegram,
            Instagram = user.Instagram,
            Messenger = user.Messenger,
            Viber = user.Viber,
            IsEmployee = user.IsEmployee,
            EmploymentStartDate = user.EmploymentStartDate?.ToString("yyyy-MM-dd"),
            EmploymentEndDate = user.EmploymentEndDate?.ToString("yyyy-MM-dd"),
            Role = new RoleDto { Id = user.Role!.Id, Name = user.Role.Name, Description = user.Role.Description },
            IsActive = user.IsActive,
            IsApproved = user.IsApproved,
            CreatedAt = user.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }

    private bool VerifyPassword(string password, string hash)
    {
        return HashPassword(password) == hash;
    }
}

public class UserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public string? DateOfBirth { get; set; }
    public string? PhotoUrl { get; set; }
    public string? PhoneNumber { get; set; }
    public string? WhatsApp { get; set; }
    public string? Telegram { get; set; }
    public string? Instagram { get; set; }
    public string? Messenger { get; set; }
    public string? Viber { get; set; }
    public bool IsEmployee { get; set; }
    public string? EmploymentStartDate { get; set; }
    public string? EmploymentEndDate { get; set; }
    public RoleDto Role { get; set; } = null!;
    public bool IsActive { get; set; }
    public bool IsApproved { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class RoleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
