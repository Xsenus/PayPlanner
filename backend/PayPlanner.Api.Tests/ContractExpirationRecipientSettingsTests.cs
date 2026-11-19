using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ContractExpirationRecipientSettingsTests(PostgreSqlModelTemplate postgresTemplate)
    : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"departmentManagersEnabled\":true,\"roleIds\":[-1]}")]
    [InlineData("{\"departmentManagersEnabled\":true,\"roleIds\":null}")]
    [InlineData("{\"departmentManagersEnabled\":true,\"departmentManagers\":[{\"departmentId\":1,\"managerUserId\":1},{\"departmentId\":1,\"managerUserId\":2}]}")]
    public void ContractExpirationSettings_InvalidStoredValueFailsClosed(string? raw)
    {
        var settings = ContractExpirationRecipientSettings.Parse(raw);
        Assert.False(settings.DepartmentManagersEnabled);
        Assert.Empty(settings.RoleIds);
        Assert.Empty(settings.UserIds);
        Assert.Empty(settings.DepartmentManagers);
        Assert.Equal(raw, settings.RawValue);
    }

    [Fact]
    public void ContractExpirationSettings_MaximumSelectionCountRoundTripsAndInternalProofIsExcluded()
    {
        var settings = new ContractExpirationRecipientSettings
        {
            DepartmentManagersEnabled = true,
            RoleIds = Enumerable.Range(1, 500).Reverse().ToArray(),
            UserIds = Enumerable.Range(501, 500).ToArray(),
            DepartmentManagers = Enumerable.Range(1, 500).Select(id => new ContractExpirationDepartmentManager
            {
                DepartmentId = id,
                ManagerUserId = id + 1000
            }).ToList(),
            RawValue = "private-read-proof"
        };
        var raw = ContractExpirationRecipientSettings.Serialize(settings);
        var stored = ContractExpirationRecipientSettings.Parse(raw);
        Assert.Equal(Enumerable.Range(1, 500), stored.RoleIds);
        Assert.Equal(500, stored.UserIds.Length);
        Assert.Equal(500, stored.DepartmentManagers.Count);
        Assert.DoesNotContain("revision", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawValue", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContractExpirationSettings_DefaultReadDoesNotWrite()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        var before = await db.SystemSettings.CountAsync();
        var dto = Ok<ContractExpirationRecipientSettingsDto>(await Controller(db, seed.Actor).GetContractExpirationSettings(default));
        Assert.False(dto.DepartmentManagersEnabled);
        Assert.Empty(dto.RoleIds);
        Assert.Empty(dto.UserIds);
        Assert.Empty(dto.DepartmentManagers);
        Assert.Empty(dto.SelectedRoles);
        Assert.Empty(dto.SelectedUsers);
        Assert.Empty(dto.SelectedDepartments);
        Assert.Equal(before, await db.SystemSettings.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContractExpirationSettings_UsesCurrentNotificationViewAndEditPermissions(bool canView, bool canEdit)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        var permission = seed.Actor.Role!.Permissions.Single(item => item.Section == RolePermissionCatalog.Notifications);
        permission.CanView = canView;
        permission.CanEdit = canEdit;
        await db.SaveChangesAsync();
        var controller = Controller(db, seed.Actor);
        var read = await controller.GetContractExpirationSettings(default);
        var options = await controller.GetContractExpirationOptions("users");
        var write = await controller.UpdateContractExpirationSettings(new(), default);
        if (canView)
        {
            Assert.IsType<OkObjectResult>(read.Result);
            Assert.IsType<OkObjectResult>(options.Result);
        }
        else
        {
            Assert.IsType<ForbidResult>(read.Result);
            Assert.IsType<ForbidResult>(options.Result);
        }
        if (canView && canEdit) Assert.IsType<OkObjectResult>(write.Result);
        else Assert.IsType<ForbidResult>(write.Result);
        Assert.Equal(canView && canEdit, await db.SystemSettings.AnyAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpirationSettings_ProviderRoundTripCombinesAllSourcesWithoutEmployeeOrUserReadRequirements(bool postgres)
    {
        await using var lease = postgres ? await postgresTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        var request = Request(seed);
        request.RoleIds = [seed.TargetRole.Id, seed.TargetRole.Id];
        request.UserIds = [seed.Staff.Id, seed.Staff.Id];
        var controller = Controller(db, seed.Actor);
        var dto = Ok<ContractExpirationRecipientSettingsDto>(await controller.UpdateContractExpirationSettings(request, default));
        Assert.True(dto.DepartmentManagersEnabled);
        Assert.Equal(new[] { seed.TargetRole.Id }, dto.RoleIds);
        Assert.Equal(new[] { seed.Staff.Id }, dto.UserIds);
        Assert.Equal(seed.Manager.Id, Assert.Single(dto.DepartmentManagers).ManagerUserId);
        Assert.Equal(new[] { seed.Staff.Id, seed.Manager.Id }.OrderBy(id => id), dto.SelectedUsers.Select(item => item.Id));
        Assert.All(dto.SelectedUsers, item => Assert.True(item.IsAvailable));
        Assert.False(seed.Staff.IsEmployee);
        Assert.False(seed.Manager.IsEmployee);
        Assert.Equal(seed.Department.Id, Assert.Single(dto.SelectedDepartments).Id);
        var options = Ok<ContractExpirationRecipientOptionsPageDto>(await controller.GetContractExpirationOptions("users", "Selected"));
        Assert.Equal(2, options.TotalCount);
        Assert.All(options.Items, item => Assert.Equal(seed.TargetRole.Id, item.RoleId));
        var stored = await ContractExpirationRecipientSettings.LoadAsync(db);
        Assert.Equal(stored.RawValue, (await db.SystemSettings.SingleAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey)).Value);
        Assert.DoesNotContain("revision", stored.RawValue!, StringComparison.OrdinalIgnoreCase);
        await controller.UpdateContractExpirationSettings(request, default);
        var revised = await ContractExpirationRecipientSettings.LoadAsync(db);
        Assert.Equal(stored.RawValue, revised.RawValue);
    }

    [Theory]
    [InlineData("negative-role")]
    [InlineData("zero-user")]
    [InlineData("negative-department")]
    [InlineData("zero-manager")]
    [InlineData("duplicate-department")]
    [InlineData("role-cap")]
    [InlineData("user-cap")]
    [InlineData("manager-cap")]
    [InlineData("null-roles")]
    [InlineData("null-users")]
    [InlineData("null-managers")]
    [InlineData("null-manager")]
    public async Task ContractExpirationSettings_InvalidPayloadLeavesSavedSettingUnchanged(string scenario)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        var controller = Controller(db, seed.Actor);
        await controller.UpdateContractExpirationSettings(Request(seed), default);
        var before = await SettingsSnapshotAsync(db);
        var request = Request(seed);
        switch (scenario)
        {
            case "negative-role": request.RoleIds = [-1]; break;
            case "zero-user": request.UserIds = [0]; break;
            case "negative-department": request.DepartmentManagers[0].DepartmentId = -1; break;
            case "zero-manager": request.DepartmentManagers[0].ManagerUserId = 0; break;
            case "duplicate-department": request.DepartmentManagers.Add(new() { DepartmentId = seed.Department.Id, ManagerUserId = seed.Staff.Id }); break;
            case "role-cap": request.RoleIds = Enumerable.Repeat(seed.TargetRole.Id, 501).ToArray(); break;
            case "user-cap": request.UserIds = Enumerable.Repeat(seed.Staff.Id, 501).ToArray(); break;
            case "manager-cap": request.DepartmentManagers = Enumerable.Range(1, 501).Select(id => new ContractExpirationDepartmentManager { DepartmentId = id, ManagerUserId = seed.Manager.Id }).ToList(); break;
            case "null-roles": request.RoleIds = null!; break;
            case "null-users": request.UserIds = null!; break;
            case "null-managers": request.DepartmentManagers = null!; break;
            case "null-manager": request.DepartmentManagers = [null!]; break;
        }
        Assert.IsType<BadRequestObjectResult>((await controller.UpdateContractExpirationSettings(request, default)).Result);
        Assert.Equal(before, await SettingsSnapshotAsync(db));
    }

    [Theory]
    [InlineData("missing-role")]
    [InlineData("missing-user")]
    [InlineData("inactive-user")]
    [InlineData("unapproved-user")]
    [InlineData("missing-department")]
    [InlineData("inactive-department")]
    [InlineData("inactive-manager")]
    public async Task ContractExpirationSettings_RejectsUnavailableSelectionsWithoutSaving(string scenario)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        var request = Request(seed);
        switch (scenario)
        {
            case "missing-role": request.RoleIds = [int.MaxValue]; break;
            case "missing-user": request.UserIds = [int.MaxValue]; break;
            case "inactive-user": seed.Staff.IsActive = false; break;
            case "unapproved-user": seed.Staff.IsApproved = false; break;
            case "missing-department": request.DepartmentManagers[0].DepartmentId = int.MaxValue; break;
            case "inactive-department": seed.Department.IsActive = false; break;
            case "inactive-manager": seed.Manager.IsActive = false; break;
        }
        await db.SaveChangesAsync();
        var before = await SettingsSnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>((await Controller(db, seed.Actor).UpdateContractExpirationSettings(request, default)).Result);
        Assert.Equal(before, await SettingsSnapshotAsync(db));
    }

    [Fact]
    public async Task ContractExpirationSettings_GetPreservesStaleIdsAndUnavailableOptionLabels()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        seed.Staff.IsActive = false;
        seed.Department.IsActive = false;
        var settings = Request(seed);
        settings.RoleIds = [int.MaxValue];
        settings.UserIds = [seed.Staff.Id, int.MaxValue];
        settings.DepartmentManagers.Add(new() { DepartmentId = int.MaxValue, ManagerUserId = seed.Manager.Id });
        db.SystemSettings.Add(new SystemSetting { Key = ContractExpirationRecipientSettings.SettingKey, Value = ContractExpirationRecipientSettings.Serialize(settings) });
        await db.SaveChangesAsync();
        var before = await SettingsSnapshotAsync(db);
        var dto = Ok<ContractExpirationRecipientSettingsDto>(await Controller(db, seed.Actor).GetContractExpirationSettings(default));
        Assert.Equal(settings.RoleIds, dto.RoleIds);
        Assert.Equal(settings.UserIds, dto.UserIds);
        var missingRole = Assert.Single(dto.SelectedRoles);
        Assert.Equal($"Роль №{int.MaxValue}", missingRole.Name);
        Assert.False(missingRole.IsAvailable);
        Assert.All(dto.SelectedUsers.Where(item => item.Id != seed.Manager.Id), item => Assert.False(item.IsAvailable));
        Assert.True(dto.SelectedUsers.Single(item => item.Id == seed.Manager.Id).IsAvailable);
        var missingUser = dto.SelectedUsers.Single(item => item.Id == int.MaxValue);
        Assert.Equal($"Сотрудник №{int.MaxValue}", missingUser.Name);
        Assert.False(missingUser.IsAvailable);
        Assert.Equal(seed.Staff.FullName, dto.SelectedUsers.Single(item => item.Id == seed.Staff.Id).Name);
        Assert.Equal(2, dto.SelectedDepartments.Count);
        Assert.All(dto.SelectedDepartments, item => Assert.False(item.IsAvailable));
        Assert.Equal(seed.Department.Name, dto.SelectedDepartments.Single(item => item.Id == seed.Department.Id).Name);
        var missingDepartment = dto.SelectedDepartments.Single(item => item.Id == int.MaxValue);
        Assert.Equal($"Подразделение №{int.MaxValue}", missingDepartment.Name);
        Assert.False(missingDepartment.IsAvailable);
        Assert.All(dto.SelectedUsers, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
        Assert.Equal(before, await SettingsSnapshotAsync(db));
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("users")]
    [InlineData("departments")]
    public async Task ContractExpirationSettings_OptionsSearchPagingAndCapAreBounded(string kind)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        for (var index = 0; index < 105; index++)
        {
            var name = $"Candidate {index:D3}";
            if (kind == "roles") db.Roles.Add(new Role { Name = name });
            else if (kind == "departments") db.Departments.Add(new DepartmentDictionaryItem { Name = name });
            else db.Users.Add(new User { FullName = name, Email = $"candidate{index}@example.test", PasswordHash = "x", Role = seed.TargetRole, IsActive = true, IsApproved = true });
        }
        await db.SaveChangesAsync();
        var controller = Controller(db, seed.Actor);
        var first = Ok<ContractExpirationRecipientOptionsPageDto>(await controller.GetContractExpirationOptions(kind, "  CANDIDATE ", pageSize: 999));
        var second = Ok<ContractExpirationRecipientOptionsPageDto>(await controller.GetContractExpirationOptions(kind, "candidate", page: 2, pageSize: 100));
        Assert.Equal(105, first.TotalCount);
        Assert.Equal(100, first.PageSize);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal(2, second.Page);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        var hugePage = Ok<ContractExpirationRecipientOptionsPageDto>(await controller.GetContractExpirationOptions(kind, "candidate", page: int.MaxValue, pageSize: 100));
        Assert.Empty(hugePage.Items);
        Assert.Equal(105, hugePage.TotalCount);
        Assert.IsType<BadRequestObjectResult>((await controller.GetContractExpirationOptions("unknown")).Result);
    }

    [Fact]
    public async Task ContractExpirationSettings_CurrentPermissionRevocationPreservesTheSavedConfiguration()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        var controller = Controller(db, seed.Actor);
        await controller.UpdateContractExpirationSettings(Request(seed), default);
        var before = await SettingsSnapshotAsync(db);
        seed.Actor.Role!.Permissions.Single(item => item.Section == RolePermissionCatalog.Notifications).CanEdit = false;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await controller.UpdateContractExpirationSettings(new(), default)).Result);
        Assert.Equal(before, await SettingsSnapshotAsync(db));
    }

    [Fact]
    public async Task ContractExpirationSettings_SelectedDirectoryReadsDoNotGrowPerRecipient()
    {
        var reads = new ReadCounter();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: reads);
        var seed = await SeedAsync(db);
        var staff = Enumerable.Range(0, 100).Select(index => new User
        {
            FullName = $"Bulk {index}",
            Email = $"bulk{index}@example.test",
            PasswordHash = "x",
            Role = seed.TargetRole,
            IsActive = true,
            IsApproved = true
        }).ToArray();
        db.Users.AddRange(staff);
        var stored = new SystemSetting { Key = ContractExpirationRecipientSettings.SettingKey, Value = ContractExpirationRecipientSettings.Serialize(Request(seed)) };
        db.SystemSettings.Add(stored);
        await db.SaveChangesAsync();
        var controller = Controller(db, seed.Actor);
        reads.Count = 0;
        await controller.GetContractExpirationSettings(default);
        var oneCount = reads.Count;
        var many = Request(seed);
        many.UserIds = staff.Select(user => user.Id).ToArray();
        stored.Value = ContractExpirationRecipientSettings.Serialize(many);
        await db.SaveChangesAsync();
        reads.Count = 0;
        var dto = Ok<ContractExpirationRecipientSettingsDto>(await controller.GetContractExpirationSettings(default));
        Assert.Equal(oneCount, reads.Count);
        Assert.Equal(101, dto.SelectedUsers.Count);
        reads.Count = 0;
        await ContractExpirationRecipientSettings.LoadAsync(db);
        Assert.Equal(1, reads.Count);
    }

    [Fact]
    public async Task ContractExpirationSettings_ImportedBusinessValueIsReadWithoutDependingOnARevisionRow()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        await Controller(db, seed.Actor).UpdateContractExpirationSettings(Request(seed), default);
        var previous = await ContractExpirationRecipientSettings.LoadAsync(db);
        var imported = Request(seed);
        imported.DepartmentManagersEnabled = false;
        imported.UserIds = [seed.Manager.Id];
        var row = await db.SystemSettings.SingleAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey);
        row.Value = ContractExpirationRecipientSettings.Serialize(imported);
        await db.SaveChangesAsync();
        var current = await ContractExpirationRecipientSettings.LoadAsync(db);
        Assert.NotEqual(previous.RawValue, current.RawValue);
        Assert.Equal(row.Value, current.RawValue);
        Assert.False(current.DepartmentManagersEnabled);
        Assert.Equal(new[] { seed.Manager.Id }, current.UserIds);
        Assert.False(await db.SystemSettings.AnyAsync(item => item.Key == "notifications.contractExpiration.recipientsRevision"));
    }

    [Theory]
    [InlineData(false, "roles")]
    [InlineData(true, "roles")]
    [InlineData(false, "users")]
    [InlineData(true, "users")]
    [InlineData(false, "departments")]
    [InlineData(true, "departments")]
    public async Task ContractExpirationSettings_OptionsPreserveCyrillicCaseInsensitiveLiteralSearchAndPaging(bool postgres, string kind)
    {
        await using var lease = postgres ? await postgresTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedAsync(db);
        if (kind == "roles")
        {
            seed.TargetRole.Name = "Юристы %_ один";
            db.Roles.AddRange(new Role { Name = "ЮРИСТЫ %_ два" }, new Role { Name = "Юристы ХХ третий" });
        }
        else if (kind == "departments")
        {
            seed.Department.Name = "Юристы %_ один";
            db.Departments.AddRange(new DepartmentDictionaryItem { Name = "ЮРИСТЫ %_ два" },
                new DepartmentDictionaryItem { Name = "Юристы ХХ третий" });
        }
        else
        {
            seed.Staff.FullName = "Юристы %_ один";
            seed.Manager.FullName = "ЮРИСТЫ %_ два";
            seed.Actor.FullName = "Юристы ХХ третий";
        }
        await db.SaveChangesAsync();
        var controller = Controller(db, seed.Actor);

        var first = Ok<ContractExpirationRecipientOptionsPageDto>(await controller.GetContractExpirationOptions(kind, "юРиСтЫ %_", 1, 1));
        var second = Ok<ContractExpirationRecipientOptionsPageDto>(await controller.GetContractExpirationOptions(kind, "юРиСтЫ %_", 2, 1));

        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        Assert.NotEqual(Assert.Single(first.Items).Id, Assert.Single(second.Items).Id);
        Assert.All(first.Items.Concat(second.Items), item =>
            Assert.True(item.Name.Contains("юРиСтЫ %_", StringComparison.OrdinalIgnoreCase)));
    }

    private static T Ok<T>(ActionResult<T> result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static NotificationsController Controller(PaymentContext db, User user) => new(db, null!, null!, null!)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "ContractExpirySettings"))
            }
        }
    };

    private static ContractExpirationRecipientSettings Request(Seed seed) => new()
    {
        DepartmentManagersEnabled = true,
        RoleIds = [seed.TargetRole.Id],
        UserIds = [seed.Staff.Id],
        DepartmentManagers = [new() { DepartmentId = seed.Department.Id, ManagerUserId = seed.Manager.Id }]
    };

    private static async Task<string> SettingsSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(await db.SystemSettings.AsNoTracking()
        .Where(item => item.Key == ContractExpirationRecipientSettings.SettingKey)
        .OrderBy(item => item.Key).Select(item => new { item.Key, item.Value, item.CreatedAt, item.UpdatedAt }).ToArrayAsync());

    private static async Task<Seed> SeedAsync(PaymentContext db)
    {
        var role = new Role
        {
            Name = "Notification editor",
            Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Notifications, CanView = true, CanEdit = true },
                new RolePermission { Section = RolePermissionCatalog.Roles, CanView = false },
                new RolePermission { Section = "users", CanView = false }
            }
        };
        var targetRole = new Role { Name = "Selected role" };
        var department = new DepartmentDictionaryItem { Name = "Selected department" };
        var actor = new User { FullName = "Actor", Email = "actor@example.test", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var staff = new User { FullName = "Selected staff", Email = "staff@example.test", PasswordHash = "x", Role = targetRole, IsActive = true, IsApproved = true, DepartmentDictionary = department };
        var manager = new User { FullName = "Selected manager", Email = "manager@example.test", PasswordHash = "x", Role = targetRole, IsActive = true, IsApproved = true };
        db.Users.AddRange(actor, staff, manager);
        await db.SaveChangesAsync();
        return new Seed(actor, targetRole, staff, manager, department);
    }

    private sealed record Seed(User Actor, Role TargetRole, User Staff, User Manager, DepartmentDictionaryItem Department);

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
