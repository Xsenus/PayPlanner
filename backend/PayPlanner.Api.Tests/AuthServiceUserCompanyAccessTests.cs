using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Xunit;

namespace PayPlanner.Api.Tests;

public class AuthServiceUserCompanyAccessTests
{
    [Fact]
    public async Task UpdateUserAsync_SavesCompanyAssignments_ForEmployee()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "user");
        var user = await CreateUserAsync(db, roleId, "employee1@example.com");
        var firstCompanyId = await CreateLegalEntityAsync(db, "Основная компания 1");
        var secondCompanyId = await CreateLegalEntityAsync(db, "Основная компания 2");
        await SaveMainCompanySettingsAsync(db, [firstCompanyId, secondCompanyId], firstCompanyId);

        var result = await service.UpdateUserAsync(
            user.Id,
            new UpdateUserRequest
            {
                IsEmployee = true,
                CompanyIds = [firstCompanyId, secondCompanyId],
            },
            actorUserId: user.Id);

        Assert.NotNull(result);
        Assert.True(result!.IsEmployee);
        Assert.Equal(new[] { firstCompanyId, secondCompanyId }, result.CompanyIds);

        var storedCompanyIds = await db.UserCompanyAccesses
            .AsNoTracking()
            .Where(item => item.UserId == user.Id)
            .Select(item => item.LegalEntityId)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal([firstCompanyId, secondCompanyId], storedCompanyIds);
    }

    [Fact]
    public async Task UpdateUserAsync_ClearsCompanyAssignments_WhenEmployeeDisabled()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "user");
        var user = await CreateUserAsync(db, roleId, "employee2@example.com", isEmployee: true);
        var companyId = await CreateLegalEntityAsync(db, "Основная компания");
        await SaveMainCompanySettingsAsync(db, [companyId], companyId);

        db.UserCompanyAccesses.Add(new UserCompanyAccess
        {
            UserId = user.Id,
            LegalEntityId = companyId,
        });
        await db.SaveChangesAsync();

        var result = await service.UpdateUserAsync(
            user.Id,
            new UpdateUserRequest
            {
                IsEmployee = false,
            },
            actorUserId: user.Id);

        Assert.NotNull(result);
        Assert.False(result!.IsEmployee);
        Assert.Empty(result.CompanyIds ?? []);

        var remainingAccesses = await db.UserCompanyAccesses
            .AsNoTracking()
            .Where(item => item.UserId == user.Id)
            .CountAsync();

        Assert.Equal(0, remainingAccesses);
    }

    [Fact]
    public async Task MergeUsersAsync_MergesCompanyAssignments_WithoutDuplicates()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "user");
        var targetUser = await CreateUserAsync(db, roleId, "target@example.com", isEmployee: true);
        var sourceUser = await CreateUserAsync(db, roleId, "source@example.com", isEmployee: true);
        var firstCompanyId = await CreateLegalEntityAsync(db, "Основная компания 1");
        var secondCompanyId = await CreateLegalEntityAsync(db, "Основная компания 2");
        await SaveMainCompanySettingsAsync(db, [firstCompanyId, secondCompanyId], firstCompanyId);

        db.UserCompanyAccesses.AddRange(
            new UserCompanyAccess { UserId = targetUser.Id, LegalEntityId = firstCompanyId },
            new UserCompanyAccess { UserId = sourceUser.Id, LegalEntityId = firstCompanyId },
            new UserCompanyAccess { UserId = sourceUser.Id, LegalEntityId = secondCompanyId });
        await db.SaveChangesAsync();

        var result = await service.MergeUsersAsync(
            new MergeUsersRequest
            {
                TargetUserId = targetUser.Id,
                SourceUserIds = [sourceUser.Id],
            },
            actorUserId: targetUser.Id,
            cancellationToken: CancellationToken.None);

        Assert.Equal(new[] { firstCompanyId, secondCompanyId }, result.CompanyIds);

        var targetAccesses = await db.UserCompanyAccesses
            .AsNoTracking()
            .Where(item => item.UserId == targetUser.Id)
            .Select(item => item.LegalEntityId)
            .OrderBy(id => id)
            .ToListAsync();
        var sourceExists = await db.Users.AsNoTracking().AnyAsync(item => item.Id == sourceUser.Id);

        Assert.Equal([firstCompanyId, secondCompanyId], targetAccesses);
        Assert.False(sourceExists);
    }

    [Fact]
    public async Task UpdateUserAsync_AssignsDepartmentFromDictionary()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "user");
        var user = await CreateUserAsync(db, roleId, "department-user@example.com");
        var department = new DepartmentDictionaryItem { Name = "Юридический отдел", IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        var result = await service.UpdateUserAsync(
            user.Id,
            new UpdateUserRequest
            {
                DepartmentId = department.Id,
            },
            actorUserId: user.Id);

        Assert.NotNull(result);
        Assert.Equal(department.Id, result!.DepartmentId);
        Assert.Equal("Юридический отдел", result.Department);

        var storedUser = await db.Users.AsNoTracking().FirstAsync(item => item.Id == user.Id);
        Assert.Equal(department.Id, storedUser.DepartmentId);
        Assert.Equal("Юридический отдел", storedUser.Department);
    }

    [Fact]
    public async Task UpdateUserAsync_ClearsDepartment_WhenEmptyDepartmentSent()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "user");
        var department = new DepartmentDictionaryItem { Name = "Секретариат", IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        var user = await CreateUserAsync(db, roleId, "department-clear@example.com");
        user.DepartmentId = department.Id;
        user.Department = department.Name;
        await db.SaveChangesAsync();

        var result = await service.UpdateUserAsync(
            user.Id,
            new UpdateUserRequest
            {
                Department = string.Empty,
            },
            actorUserId: user.Id);

        Assert.NotNull(result);
        Assert.Null(result!.DepartmentId);
        Assert.Null(result.Department);

        var storedUser = await db.Users.AsNoTracking().FirstAsync(item => item.Id == user.Id);
        Assert.Null(storedUser.DepartmentId);
        Assert.Null(storedUser.Department);
    }

    [Fact]
    public async Task UpdateUserAsync_ClearsOptionalContactsAndDates_WhenRequested()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "user-clear-fields");
        var user = await CreateUserAsync(db, roleId, "clear-fields@example.com");
        user.FirstName = "Иван";
        user.PhoneNumber = "+79990000000";
        user.WhatsApp = "+79990000000";
        user.DateOfBirth = new DateTime(1990, 1, 10);
        user.EmploymentStartDate = new DateTime(2025, 1, 1);
        user.EmploymentEndDate = new DateTime(2026, 1, 1);
        await db.SaveChangesAsync();

        var result = await service.UpdateUserAsync(
            user.Id,
            new UpdateUserRequest
            {
                FirstName = string.Empty,
                PhoneNumber = string.Empty,
                WhatsApp = string.Empty,
                ClearDateOfBirth = true,
                ClearEmploymentStartDate = true,
                ClearEmploymentEndDate = true,
            },
            actorUserId: user.Id);

        Assert.NotNull(result);
        Assert.Null(result!.FirstName);
        Assert.Null(result.PhoneNumber);
        Assert.Null(result.WhatsApp);
        Assert.Null(result.DateOfBirth);
        Assert.Null(result.EmploymentStartDate);
        Assert.Null(result.EmploymentEndDate);

        var storedUser = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.Null(storedUser.FirstName);
        Assert.Null(storedUser.PhoneNumber);
        Assert.Null(storedUser.WhatsApp);
        Assert.Null(storedUser.DateOfBirth);
        Assert.Null(storedUser.EmploymentStartDate);
        Assert.Null(storedUser.EmploymentEndDate);
    }

    [Fact]
    public async Task UpdateProfileAsync_ClearsOptionalContactsAndDate_WhenRequested()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "profile-clear-fields");
        var user = await CreateUserAsync(db, roleId, "profile-clear@example.com");
        user.PhoneNumber = "+79990000001";
        user.DateOfBirth = new DateTime(1991, 2, 11);
        await db.SaveChangesAsync();

        var result = await service.UpdateProfileAsync(
            user.Id,
            new UpdateProfileRequest
            {
                PhoneNumber = string.Empty,
                ClearDateOfBirth = true,
            });

        Assert.NotNull(result);
        Assert.Null(result!.PhoneNumber);
        Assert.Null(result.DateOfBirth);
    }

    [Fact]
    public async Task UpdateUserAsync_UsesUserTimeZoneForDateOfBirthFutureValidation()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db);
        var roleId = await CreateRoleAsync(db, "timezone-user");
        var user = await CreateUserAsync(db, roleId, "timezone-birth@example.com");
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.TimeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        if (selectedZone.ProfileToday > selectedZone.UtcToday)
        {
            var result = await service.UpdateUserAsync(
                user.Id,
                new UpdateUserRequest
                {
                    DateOfBirth = selectedZone.ProfileToday,
                },
                actorUserId: user.Id);

            Assert.NotNull(result);
            Assert.Equal(selectedZone.ProfileToday, result!.DateOfBirth);

            var storedUser = await db.Users.AsNoTracking().FirstAsync(item => item.Id == user.Id);
            Assert.Equal(selectedZone.ProfileToday, storedUser.DateOfBirth);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateUserAsync(
                user.Id,
                new UpdateUserRequest
                {
                    DateOfBirth = selectedZone.UtcToday,
                },
                actorUserId: user.Id));

            Assert.Equal("DateOfBirth cannot be in the future.", error.Message);

            var storedUser = await db.Users.AsNoTracking().FirstAsync(item => item.Id == user.Id);
            Assert.Null(storedUser.DateOfBirth);
        }
    }

    [Fact]
    public async Task RefreshSessionAsync_IssuesNewTokenForActiveApprovedUser()
    {
        await using var db = await CreateContextAsync();
        var roleId = await CreateRoleAsync(db, "manager");
        var user = await CreateUserAsync(db, roleId, "session-refresh@example.com");
        var service = CreateService(db);

        var response = await service.RefreshSessionAsync(user.Id, user.AuthTokenVersion);

        Assert.NotNull(response);
        Assert.Equal(user.Id, response!.User.Id);
        Assert.Equal("manager", response.User.Role.Name);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.Token);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(claim => claim.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal(
            user.AuthTokenVersion.ToString(),
            jwt.Claims.Single(claim => claim.Type == AuthSessionClaims.TokenVersionClaimType).Value);
        Assert.True(jwt.ValidTo > DateTime.UtcNow.AddDays(6));
    }

    private static AuthService CreateService(PaymentContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-session-refresh-secret-with-at-least-thirty-two-characters",
                ["Jwt:Issuer"] = "PayPlanner.Tests",
                ["Jwt:Audience"] = "PayPlanner.Tests",
            })
            .Build();

        return new AuthService(
            db,
            configuration,
            new NoopEmailService(),
            new FakeWebHostEnvironment(),
            new PasswordHasher<User>());
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<int> CreateRoleAsync(PaymentContext db, string name)
    {
        var role = new Role
        {
            Name = name,
            Description = name,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, int roleId, string email, bool isEmployee = false)
    {
        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = email,
            RoleId = roleId,
            IsEmployee = isEmployee,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static (string TimeZoneId, DateTime UtcToday, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, utcToday, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC date was found.");
    }

    private static async Task<int> CreateLegalEntityAsync(PaymentContext db, string shortName)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            FullName = shortName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task SaveMainCompanySettingsAsync(PaymentContext db, IReadOnlyCollection<int> companyIds, int? defaultCompanyId)
    {
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = System.Text.Json.JsonSerializer.Serialize(companyIds.OrderBy(id => id).ToArray()),
        });

        if (defaultCompanyId.HasValue)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = "legalEntities.defaultMainCompanyId",
                Value = defaultCompanyId.Value.ToString(),
            });
            db.SystemSettings.Add(new SystemSetting
            {
                Key = "legalEntities.mainCompanyId",
                Value = defaultCompanyId.Value.ToString(),
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
