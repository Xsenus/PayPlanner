using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Auth;

namespace PayPlanner.Api.Services;

public class AuthService
{
    private readonly PaymentContext _context;
    private readonly IConfiguration _configuration;

    public AuthService(PaymentContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        var user = await _context.Set<User>()
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
        {
            return null;
        }

        if (!user.IsActive || !user.IsActivated)
        {
            return null;
        }

        var token = GenerateJwtToken(user);
        var userDto = MapToDto(user);

        return new AuthResponse
        {
            Token = token,
            User = userDto
        };
    }

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        var existingUser = await _context.Set<User>()
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (existingUser != null)
        {
            return null;
        }

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            Email = request.Email,
            PasswordHash = HashPassword(request.Password),
            FullName = request.FullName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Set<User>().Add(user);
        await _context.SaveChangesAsync();

        var userRole = await _context.Set<Role>()
            .FirstOrDefaultAsync(r => r.Name == "user");

        if (userRole != null)
        {
            var assignment = new UserRole
            {
                UserId = user.Id,
                RoleId = userRole.Id,
                AssignedAt = DateTime.UtcNow
            };
            _context.Set<UserRole>().Add(assignment);
            await _context.SaveChangesAsync();
        }

        var fullUser = await _context.Set<User>()
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstAsync(u => u.Id == user.Id);

        var token = GenerateJwtToken(fullUser);
        var userDto = MapToDto(fullUser);

        return new AuthResponse
        {
            Token = token,
            User = userDto
        };
    }

    public async Task<UserDto?> GetUserByIdAsync(string userId)
    {
        var user = await _context.Set<User>()
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);

        return user != null ? MapToDto(user) : null;
    }

    public async Task<List<UserDto>> GetAllUsersAsync()
    {
        var users = await _context.Set<User>()
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Email)
            .ToListAsync();

        return users.Select(MapToDto).ToList();
    }

    public async Task<bool> UpdateUserAsync(string userId, string? fullName, bool? isActive)
    {
        var user = await _context.Set<User>()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return false;
        }

        if (fullName != null)
        {
            user.FullName = fullName;
        }

        if (isActive.HasValue)
        {
            user.IsActive = isActive.Value;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> ChangePasswordAsync(string userId, string newPassword)
    {
        var user = await _context.Set<User>()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return false;
        }

        user.PasswordHash = HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> AssignRoleAsync(string userId, int roleId)
    {
        var existingAssignment = await _context.Set<UserRole>()
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId);

        if (existingAssignment != null)
        {
            return true;
        }

        var userRole = new UserRole
        {
            UserId = userId,
            RoleId = roleId,
            AssignedAt = DateTime.UtcNow
        };

        _context.Set<UserRole>().Add(userRole);
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> RemoveRoleAsync(string userId, int roleId)
    {
        var userRole = await _context.Set<UserRole>()
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId);

        if (userRole == null)
        {
            return false;
        }

        _context.Set<UserRole>().Remove(userRole);
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<List<Role>> GetAllRolesAsync()
    {
        return await _context.Set<Role>()
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<bool> ActivateUserAsync(string userId)
    {
        var user = await _context.Set<User>()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return false;
        }

        user.IsActivated = true;
        user.IsActive = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeactivateUserAsync(string userId)
    {
        var user = await _context.Set<User>()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return false;
        }

        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeleteUserAsync(string userId)
    {
        var user = await _context.Set<User>()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return false;
        }

        _context.Set<User>().Remove(user);
        await _context.SaveChangesAsync();

        return true;
    }

    private string GenerateJwtToken(User user)
    {
        var jwtKey = _configuration["Jwt:Key"] ?? "your-super-secret-key-min-32-chars-long-12345";
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "PayPlanner";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "PayPlanner";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("full_name", user.FullName)
        };

        foreach (var userRole in user.UserRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, userRole.Role.Name));
        }

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    private static bool VerifyPassword(string password, string passwordHash)
    {
        return BCrypt.Net.BCrypt.Verify(password, passwordHash);
    }

    private static UserDto MapToDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Roles = user.UserRoles.Select(ur => ur.Role.Name).ToList(),
            CreatedAt = user.CreatedAt
        };
    }
}
