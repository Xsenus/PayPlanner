using System.Data.Common;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DataVisibilityServiceTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_section_access_test_";

    [Fact]
    public async Task ResolveSectionAccessAsync_ReturnsPermissionAndOwnVisibilityInOneQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var role = new Role { Name = "case-editor", Description = "case-editor" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = true,
            CanViewAnalytics = false,
            ViewScope = DataVisibilityScope.Own,
            CanEditOthers = false,
            CanCompleteOthers = true,
            CanDeleteOthers = false,
        });
        var user = CreateUser("section-access@example.com", "Редактор дел", role.Id);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        interceptor.ReaderCommands.Clear();

        var access = await db.ResolveSectionAccessAsync(
            CreatePrincipal(user.Id),
            RolePermissionCatalog.Cases);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Own, access!.Visibility.ViewScope);
        Assert.Equal(new[] { user.Id }, access.Visibility.VisibleUserIds);
        var permission = Assert.IsType<RolePermissionValues>(access.Permission);
        Assert.True(permission.CanCreate);
        Assert.True(permission.CanEdit);
        Assert.True(permission.CanExport);
        Assert.True(permission.CanCompleteOthers);
        Assert.False(permission.CanDelete);
        Assert.False(permission.CanViewAnalytics);
        var sql = Assert.Single(interceptor.ReaderCommands);
        Assert.Contains("RolePermissions", sql, StringComparison.Ordinal);
        Assert.InRange(CountOccurrences(sql, "RolePermissions"), 1, 2);
    }

    [Fact]
    public async Task ResolveSectionAccessAsync_LoadsDepartmentMembersWithSecondBoundedQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var role = new Role { Name = "department-lawyer", Description = "department-lawyer" };
        var department = new DepartmentDictionaryItem { Name = "Арбитраж", IsActive = true };
        var otherDepartment = new DepartmentDictionaryItem { Name = "Исполнение", IsActive = true };
        db.AddRange(role, department, otherDepartment);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(CreatePermission(
            role.Id,
            RolePermissionCatalog.Cases,
            DataVisibilityScope.Department));
        var current = CreateUser("department-current@example.com", "Юрист отдела", role.Id, department);
        var teammate = CreateUser("department-teammate@example.com", "Коллега отдела", role.Id, department);
        var outsider = CreateUser("department-outsider@example.com", "Другой отдел", role.Id, otherDepartment);
        db.Users.AddRange(current, teammate, outsider);
        await db.SaveChangesAsync();
        interceptor.ReaderCommands.Clear();

        var access = await db.ResolveSectionAccessAsync(
            CreatePrincipal(current.Id),
            RolePermissionCatalog.Cases);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Department, access!.Visibility.ViewScope);
        Assert.True(access.Visibility.CanSeeUser(current.Id));
        Assert.True(access.Visibility.CanSeeUser(teammate.Id));
        Assert.False(access.Visibility.CanSeeUser(outsider.Id));
        Assert.Equal(2, interceptor.ReaderCommands.Count);
        Assert.Contains(interceptor.ReaderCommands, sql => sql.Contains("RolePermissions", StringComparison.Ordinal));
        Assert.Contains(interceptor.ReaderCommands, sql => sql.Contains("Users", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveSectionAccessAsync_UsesDatabaseAdminRoleAndRejectsMissingPermission()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role { Name = "admin", Description = "admin" };
        var deniedRole = new Role { Name = "no-cases", Description = "no-cases" };
        db.Roles.AddRange(adminRole, deniedRole);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(CreatePermission(
            deniedRole.Id,
            RolePermissionCatalog.Tasks,
            DataVisibilityScope.Own));
        var admin = CreateUser("section-admin@example.com", "Администратор", adminRole.Id);
        var denied = CreateUser("section-denied@example.com", "Без права дел", deniedRole.Id);
        db.Users.AddRange(admin, denied);
        await db.SaveChangesAsync();
        interceptor.ReaderCommands.Clear();

        var adminAccess = await db.ResolveSectionAccessAsync(
            CreatePrincipal(admin.Id),
            RolePermissionCatalog.Cases);
        var deniedAccess = await db.ResolveSectionAccessAsync(
            CreatePrincipal(denied.Id, isAdminClaim: true),
            RolePermissionCatalog.Cases);

        Assert.NotNull(adminAccess);
        Assert.True(adminAccess!.Visibility.SeesAll);
        var adminPermission = Assert.IsType<RolePermissionValues>(adminAccess.Permission);
        Assert.True(adminPermission.CanView);
        Assert.True(adminPermission.CanCreate);
        Assert.True(adminPermission.CanEdit);
        Assert.True(adminPermission.CanDelete);
        Assert.Null(deniedAccess);
        Assert.Equal(2, interceptor.ReaderCommands.Count);
    }

    [Fact]
    public async Task ResolveSectionAccessAsync_PreservesLegacyOwnVisibilityWithoutOperationPermission()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var emptyRole = new Role { Name = "legacy-empty-role", Description = "legacy-empty-role" };
        db.Roles.Add(emptyRole);
        await db.SaveChangesAsync();
        var user = CreateUser("legacy-section-access@example.com", "Сотрудник старой роли", emptyRole.Id);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        interceptor.ReaderCommands.Clear();

        var access = await db.ResolveSectionAccessAsync(
            CreatePrincipal(user.Id),
            RolePermissionCatalog.Cases);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Own, access!.Visibility.ViewScope);
        Assert.Equal(new[] { user.Id }, access.Visibility.VisibleUserIds);
        Assert.Null(access.Permission);
        Assert.Single(interceptor.ReaderCommands);
    }

    [Fact]
    public async Task ResolveSectionAccessAsync_TranslatesOnFreshPostgreSqlDatabase()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = PostgreSqlDatabasePrefix + Guid.NewGuid().ToString("N");
        Assert.StartsWith(PostgreSqlDatabasePrefix, databaseName, StringComparison.Ordinal);
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var interceptor = new RecordingCommandInterceptor();
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .AddInterceptors(interceptor)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            await db.Database.MigrateAsync();
            var role = new Role { Name = "postgres-case-viewer", Description = "postgres-case-viewer" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(CreatePermission(
                role.Id,
                RolePermissionCatalog.Cases,
                DataVisibilityScope.Own));
            var user = CreateUser("postgres-section-access@example.com", "PostgreSQL пользователь", role.Id);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            interceptor.ReaderCommands.Clear();

            var access = await db.ResolveSectionAccessAsync(
                CreatePrincipal(user.Id),
                RolePermissionCatalog.Cases);

            Assert.NotNull(access);
            Assert.Equal(DataVisibilityScope.Own, access!.Visibility.ViewScope);
            Assert.Equal(new[] { user.Id }, access.Visibility.VisibleUserIds);
            var sql = Assert.Single(interceptor.ReaderCommands);
            Assert.InRange(CountOccurrences(sql, "RolePermissions"), 1, 2);
        }
        finally
        {
            await using var terminate = adminConnection.CreateCommand();
            terminate.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ResolveDataVisibilitiesForUsersAsync_BatchesRoleAndDepartmentQueries()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role { Name = "admin", Description = "admin" };
        var lawyerRole = new Role { Name = "lawyer", Description = "lawyer" };
        var deniedRole = new Role { Name = "denied", Description = "denied" };
        var department = new DepartmentDictionaryItem { Name = "Судебный отдел", IsActive = true };
        db.Roles.AddRange(adminRole, lawyerRole, deniedRole);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            CreatePermission(lawyerRole.Id, RolePermissionCatalog.Cases, DataVisibilityScope.Department),
            CreatePermission(deniedRole.Id, RolePermissionCatalog.Cases, DataVisibilityScope.Own, canView: false));
        var admin = CreateUser("batch-admin@example.com", "Администратор", adminRole.Id);
        var lawyer = CreateUser("batch-lawyer@example.com", "Юрист", lawyerRole.Id, department);
        var teammate = CreateUser("batch-teammate@example.com", "Коллега", lawyerRole.Id, department);
        var denied = CreateUser("batch-denied@example.com", "Без доступа", deniedRole.Id);
        var inactive = CreateUser("batch-inactive@example.com", "Отключён", lawyerRole.Id, department);
        inactive.IsActive = false;
        db.Users.AddRange(admin, lawyer, teammate, denied, inactive);
        await db.SaveChangesAsync();
        interceptor.ReaderCommands.Clear();

        var access = await db.ResolveDataVisibilitiesForUsersAsync(
            new[] { admin.Id, lawyer.Id, denied.Id, inactive.Id },
            RolePermissionCatalog.Cases);

        Assert.True(access[admin.Id]!.SeesAll);
        Assert.Equal(DataVisibilityScope.Department, access[lawyer.Id]!.ViewScope);
        Assert.True(access[lawyer.Id]!.CanSeeUser(teammate.Id));
        Assert.Null(access[denied.Id]);
        Assert.Null(access[inactive.Id]);
        Assert.Equal(2, interceptor.ReaderCommands.Count);
    }

    [Fact]
    public async Task ResolveDataVisibilitiesAsync_LoadsSharedAccessDataOnceAndPreservesSectionScopes()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var role = new Role { Name = "manager", Description = "manager" };
        var department = new DepartmentDictionaryItem { Name = "Продажи", IsActive = true };
        db.Roles.Add(role);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            CreatePermission(role.Id, RolePermissionCatalog.Tasks, DataVisibilityScope.Own),
            CreatePermission(role.Id, RolePermissionCatalog.Cases, DataVisibilityScope.Department),
            CreatePermission(role.Id, RolePermissionCatalog.Accounts, DataVisibilityScope.All),
            CreatePermission(role.Id, RolePermissionCatalog.Payments, DataVisibilityScope.Own, canView: false),
            CreatePermission(role.Id, RolePermissionCatalog.Contracts, DataVisibilityScope.Own));
        var current = new User
        {
            Email = "batch-current@example.com",
            PasswordHash = "hash",
            FullName = "Batch Current",
            RoleId = role.Id,
            DepartmentId = department.Id,
            Department = department.Name,
            IsActive = true,
        };
        var teammate = new User
        {
            Email = "batch-teammate@example.com",
            PasswordHash = "hash",
            FullName = "Batch Teammate",
            RoleId = role.Id,
            DepartmentId = department.Id,
            Department = department.Name,
            IsActive = true,
        };
        db.Users.AddRange(current, teammate);
        await db.SaveChangesAsync();
        interceptor.ReaderCommands.Clear();

        var principal = CreatePrincipal(current.Id);
        var sections = new[]
        {
            RolePermissionCatalog.Tasks,
            RolePermissionCatalog.Cases,
            RolePermissionCatalog.Accounts,
            RolePermissionCatalog.Payments,
            RolePermissionCatalog.Acts,
            RolePermissionCatalog.Contracts,
        };
        foreach (var section in sections)
        {
            await db.ResolveDataVisibilityAsync(principal, section);
        }

        Assert.Equal(14, interceptor.ReaderCommands.Count);
        interceptor.ReaderCommands.Clear();

        var access = await db.ResolveDataVisibilitiesAsync(principal, sections);

        Assert.Equal(3, interceptor.ReaderCommands.Count);
        Assert.Equal(DataVisibilityScope.Own, access[RolePermissionCatalog.Tasks]!.ViewScope);
        Assert.Equal(DataVisibilityScope.Department, access[RolePermissionCatalog.Cases]!.ViewScope);
        Assert.True(access[RolePermissionCatalog.Cases]!.CanSeeUser(teammate.Id));
        Assert.Equal(DataVisibilityScope.All, access[RolePermissionCatalog.Accounts]!.ViewScope);
        Assert.Null(access[RolePermissionCatalog.Payments]);
        Assert.Null(access[RolePermissionCatalog.Acts]);
        Assert.Equal(DataVisibilityScope.Own, access[RolePermissionCatalog.Contracts]!.ViewScope);
    }

    [Fact]
    public async Task IsUserAdminOrLegacyFallbackAsync_UsesDatabaseRoleForExistingUser()
    {
        await using var db = await CreateContextAsync();
        var adminRole = new Role { Name = "admin", Description = "admin" };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var admin = new User
        {
            Email = "db-admin@example.com",
            PasswordHash = "hash",
            FullName = "Database Admin",
            RoleId = adminRole.Id,
            IsActive = true,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var isAdmin = await db.IsUserAdminOrLegacyFallbackAsync(CreatePrincipal(admin.Id));

        Assert.True(isAdmin);
    }

    [Fact]
    public async Task IsUserAdminOrLegacyFallbackAsync_IgnoresAdminClaimForExistingNonAdminUser()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "manager", Description = "manager" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User
        {
            Email = "db-manager@example.com",
            PasswordHash = "hash",
            FullName = "Database Manager",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var isAdmin = await db.IsUserAdminOrLegacyFallbackAsync(CreatePrincipal(user.Id, isAdminClaim: true));

        Assert.False(isAdmin);
    }

    [Fact]
    public async Task IsUserAdminOrLegacyFallbackAsync_ReturnsFalseForMissingUserWhenDatabaseHasUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "manager", Description = "manager" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            Email = "existing-admin-helper-user@example.com",
            PasswordHash = "hash",
            FullName = "Existing User",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var isAdmin = await db.IsUserAdminOrLegacyFallbackAsync(CreatePrincipal(987654, isAdminClaim: true));

        Assert.False(isAdmin);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_IgnoresAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "lawyer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var user = new User
        {
            Email = "stale-admin-claim@example.com",
            PasswordHash = "hash",
            FullName = "Stale Admin Claim",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, isAdminClaim: true);

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Cases);

        Assert.NotNull(access);
        Assert.False(access!.IsAdmin);
        Assert.Equal(DataVisibilityScope.Own, access.ViewScope);
        Assert.False(access.CanSeeUser(user.Id + 1000));
    }

    [Fact]
    public async Task HasUserSectionPermissionAsync_IgnoresAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer", Description = "viewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            ViewScope = DataVisibilityScope.Own,
        });

        var user = new User
        {
            Email = "stale-section-admin@example.com",
            PasswordHash = "hash",
            FullName = "Stale Section Admin",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, isAdminClaim: true);

        var canCreate = await db.HasUserSectionPermissionAsync(
            principal,
            RolePermissionCatalog.Cases,
            static permission => permission.CanCreate);

        Assert.False(canCreate);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_ReturnsNullForMissingUserWhenDatabaseHasUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "lawyer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            Email = "existing-user@example.com",
            PasswordHash = "hash",
            FullName = "Existing User",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var access = await db.ResolveDataVisibilityAsync(
            CreatePrincipal(987654, isAdminClaim: true),
            RolePermissionCatalog.Cases);

        Assert.Null(access);
    }

    [Fact]
    public async Task HasUserSectionPermissionAsync_ReturnsFalseForMissingUserWhenDatabaseHasUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer", Description = "viewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            Email = "existing-section-user@example.com",
            PasswordHash = "hash",
            FullName = "Existing Section User",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var canCreate = await db.HasUserSectionPermissionAsync(
            CreatePrincipal(987654, isAdminClaim: true),
            RolePermissionCatalog.Cases,
            static permission => permission.CanCreate);

        Assert.False(canCreate);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_UsesDepartmentDictionaryAndLegacyNameFallback()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "secretary" };
        var department = new DepartmentDictionaryItem { Name = "Секретариат", IsActive = true };
        var anotherDepartment = new DepartmentDictionaryItem { Name = "Юристы", IsActive = true };
        db.Roles.Add(role);
        db.Departments.AddRange(department, anotherDepartment);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            ViewScope = DataVisibilityScope.Department,
        });

        var current = new User
        {
            Email = "current@example.com",
            PasswordHash = "hash",
            FullName = "Current",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        var teammate = new User
        {
            Email = "teammate@example.com",
            PasswordHash = "hash",
            FullName = "Teammate",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        var legacyTeammate = new User
        {
            Email = "legacy@example.com",
            PasswordHash = "hash",
            FullName = "Legacy",
            RoleId = role.Id,
            IsActive = true,
            Department = department.Name,
        };
        var other = new User
        {
            Email = "other@example.com",
            PasswordHash = "hash",
            FullName = "Other",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = anotherDepartment.Id,
            Department = anotherDepartment.Name,
        };
        db.Users.AddRange(current, teammate, legacyTeammate, other);
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, current.Id.ToString()),
        }, "test"));

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Clients);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Department, access!.ViewScope);
        Assert.Contains(current.Id, access.VisibleUserIds);
        Assert.Contains(teammate.Id, access.VisibleUserIds);
        Assert.Contains(legacyTeammate.Id, access.VisibleUserIds);
        Assert.DoesNotContain(other.Id, access.VisibleUserIds);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_DoesNotUseClientPermissionFallbackForCases()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "lawyer" };
        var department = new DepartmentDictionaryItem { Name = "Юридический отдел", IsActive = true };
        db.Roles.Add(role);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            CanCreate = true,
            CanEdit = false,
            ViewScope = DataVisibilityScope.Department,
        });

        var current = new User
        {
            Email = "current-cases@example.com",
            PasswordHash = "hash",
            FullName = "Current Cases",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        var teammate = new User
        {
            Email = "teammate-cases@example.com",
            PasswordHash = "hash",
            FullName = "Teammate Cases",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        db.Users.AddRange(current, teammate);
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, current.Id.ToString()),
        }, "test"));

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Cases);
        var permission = await db.GetUserSectionPermissionAsync(current.Id, RolePermissionCatalog.Cases);

        Assert.Null(access);
        Assert.Null(permission);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_UsesSpecificCalendarUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            ViewScope = DataVisibilityScope.Specific,
        });

        var current = new User
        {
            Email = "calendar-current@example.com",
            PasswordHash = "hash",
            FullName = "Current",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var selected = new User
        {
            Email = "calendar-selected@example.com",
            PasswordHash = "hash",
            FullName = "Selected",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var notSelected = new User
        {
            Email = "calendar-not-selected@example.com",
            PasswordHash = "hash",
            FullName = "Not Selected",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var inactiveSelected = new User
        {
            Email = "calendar-inactive@example.com",
            PasswordHash = "hash",
            FullName = "Inactive",
            RoleId = role.Id,
            IsActive = false,
            IsApproved = true,
        };
        db.Users.AddRange(current, selected, notSelected, inactiveSelected);
        await db.SaveChangesAsync();

        db.RoleCalendarVisibleUsers.AddRange(
            new RoleCalendarVisibleUser { RoleId = role.Id, UserId = selected.Id },
            new RoleCalendarVisibleUser { RoleId = role.Id, UserId = inactiveSelected.Id });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, current.Id.ToString()),
        }, "test"));

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Calendar);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Specific, access!.ViewScope);
        Assert.Contains(current.Id, access.VisibleUserIds);
        Assert.Contains(selected.Id, access.VisibleUserIds);
        Assert.DoesNotContain(notSelected.Id, access.VisibleUserIds);
        Assert.DoesNotContain(inactiveSelected.Id, access.VisibleUserIds);
    }

    private static RolePermission CreatePermission(
        int roleId,
        string section,
        string viewScope,
        bool canView = true)
        => new()
        {
            RoleId = roleId,
            Section = section,
            CanView = canView,
            ViewScope = viewScope,
        };

    private static User CreateUser(
        string email,
        string fullName,
        int roleId,
        DepartmentDictionaryItem? department = null)
        => new()
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            RoleId = roleId,
            DepartmentId = department?.Id,
            Department = department?.Name,
            IsActive = true,
            IsApproved = true,
        };

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static ClaimsPrincipal CreatePrincipal(int userId, bool isAdminClaim = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (isAdminClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static int CountOccurrences(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;
}
