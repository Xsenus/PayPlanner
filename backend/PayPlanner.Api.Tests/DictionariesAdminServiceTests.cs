using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DictionariesAdminServiceTests
{
    [Fact]
    public async Task CreateDepartment_RefreshesLinkedUsersAndAffectedPeersAfterSave()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "department-viewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = " Department " });
        var otherDepartment = new DepartmentDictionaryItem { Name = "Existing", IsActive = true };
        db.Departments.Add(otherDepartment);
        await db.SaveChangesAsync();
        var linked = new User { Email = "linked@local", FullName = "Linked", RoleId = role.Id, IsActive = true, IsApproved = true, Department = "New department" };
        var peer = new User { Email = "peer@local", FullName = "Peer", RoleId = role.Id, IsActive = true, IsApproved = true, Department = "New department", DepartmentId = otherDepartment.Id };
        var unrelated = new User { Email = "other@local", FullName = "Other", RoleId = role.Id, IsActive = true, IsApproved = true, Department = "Other" };
        db.Users.AddRange(linked, peer, unrelated);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher(db);

        var result = await new DictionariesAdminController(db, publisher).CreateDepartment(new DepartmentDictionaryItem { Name = "New department", IsActive = true });

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(new[] { linked.Id, peer.Id }.Order(), Assert.Single(publisher.Batches).Order());
        Assert.True(publisher.SavedBeforePublish);
        Assert.NotNull((await db.Users.AsNoTracking().SingleAsync(user => user.Id == linked.Id)).DepartmentId);
    }

    [Fact]
    public async Task UpdateDepartment_RefreshesOldAndNewDepartmentsAfterReassignment()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "department-viewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Department });
        var targetDepartment = new DepartmentDictionaryItem { Name = "Old name", IsActive = true };
        var sourceDepartment = new DepartmentDictionaryItem { Name = "Source", IsActive = true };
        db.Departments.AddRange(targetDepartment, sourceDepartment);
        await db.SaveChangesAsync();
        var moved = new User { Email = "moved@local", FullName = "Moved", RoleId = role.Id, IsActive = true, IsApproved = true, DepartmentId = sourceDepartment.Id, Department = "Old name" };
        var sourcePeer = new User { Email = "source@local", FullName = "Source peer", RoleId = role.Id, IsActive = true, IsApproved = true, DepartmentId = sourceDepartment.Id, Department = "Source" };
        var targetPeer = new User { Email = "target@local", FullName = "Target peer", RoleId = role.Id, IsActive = true, IsApproved = true, DepartmentId = targetDepartment.Id, Department = "Old name" };
        var unrelated = new User { Email = "unrelated@local", FullName = "Unrelated", RoleId = role.Id, IsActive = true, IsApproved = true, Department = "Unrelated" };
        db.Users.AddRange(moved, sourcePeer, targetPeer, unrelated);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher(db);

        var result = await new DictionariesAdminController(db, publisher).UpdateDepartment(targetDepartment.Id, new DepartmentDictionaryItem { Name = "New name", IsActive = true });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { moved.Id, sourcePeer.Id, targetPeer.Id }.Order(), Assert.Single(publisher.Batches).Order());
        Assert.True(publisher.SavedBeforePublish);
        Assert.Equal(targetDepartment.Id, (await db.Users.AsNoTracking().SingleAsync(user => user.Id == moved.Id)).DepartmentId);
    }

    [Fact]
    public async Task UpdateDepartment_MetadataOnlyDoesNotNotifyUsers()
    {
        await using var db = await CreateContextAsync();
        var department = new DepartmentDictionaryItem { Name = "Department", IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher(db);
        var controller = new DictionariesAdminController(db, publisher);
        Assert.IsType<OkObjectResult>(await controller.UpdateDepartment(department.Id, new DepartmentDictionaryItem { Name = "Department", Description = "New description", IsActive = true }));
        Assert.Empty(publisher.Batches);
    }

    [Fact]
    public async Task CreateDepartment_InvalidNameDoesNotPublishOrSave()
    {
        await using var db = await CreateContextAsync();
        var publisher = new RecordingPublisher(db);
        var result = await new DictionariesAdminController(db, publisher).CreateDepartment(new DepartmentDictionaryItem { Name = "   " });
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(publisher.Batches);
        Assert.Empty(await db.Departments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateDepartment_PublisherFailureDoesNotUndoSavedChanges()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "department-viewer" };
        var department = new DepartmentDictionaryItem { Name = "Before", IsActive = true };
        db.AddRange(role, department);
        await db.SaveChangesAsync();
        var user = new User { Email = "member@local", FullName = "Member", RoleId = role.Id, IsActive = true, IsApproved = true, DepartmentId = department.Id, Department = department.Name };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await new DictionariesAdminController(db, new ThrowingPublisher()).UpdateDepartment(department.Id, new DepartmentDictionaryItem { Name = "After", IsActive = true });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("After", (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).Department);
        Assert.Equal("After", (await db.Departments.AsNoTracking().SingleAsync(item => item.Id == department.Id)).Name);
    }

    private sealed class ThrowingPublisher : IUserRoleChangePublisher
    {
        public Task PublishUserRoleChangedAsync(int userId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Publisher unavailable");
        public Task PublishUsersRoleChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Publisher unavailable");
    }

    private sealed class RecordingPublisher(PaymentContext db) : IUserRoleChangePublisher
    {
        public List<int[]> Batches { get; } = [];
        public bool SavedBeforePublish { get; private set; }
        public Task PublishUserRoleChangedAsync(int userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PublishUsersRoleChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
        {
            Batches.Add(userIds.ToArray());
            SavedBeforePublish = !db.ChangeTracker.HasChanges();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void ServiceDictionaryName_MaxLength_AllowsLongCourtServiceTitles()
    {
        using var db = CreateContext();

        var nameMaxLength = db.Model
            .FindEntityType(typeof(ServiceDictionaryItem))!
            .FindProperty(nameof(ServiceDictionaryItem.Name))!
            .GetMaxLength();
        var descriptionMaxLength = db.Model
            .FindEntityType(typeof(ServiceDictionaryItem))!
            .FindProperty(nameof(ServiceDictionaryItem.Description))!
            .GetMaxLength();

        Assert.Equal(1000, nameMaxLength);
        Assert.Equal(2000, descriptionMaxLength);
    }

    [Fact]
    public async Task CreateService_PersistsLongCourtServiceTitle()
    {
        await using var db = await CreateContextAsync();
        var category = new ServiceCategoryDictionaryItem
        {
            Name = "Administrative cases",
            Description = string.Empty,
            ColorHex = "#0EA5E9",
            IsActive = true,
        };
        db.ServiceCategories.Add(category);
        await db.SaveChangesAsync();

        var longName = "ГР/ получение/подача документа в суд либо в иные Государственные, Муниципальные, Административные органы власти";
        Assert.True(longName.Length > 100);

        var controller = new DictionariesAdminController(db);
        var result = await controller.CreateService(new ServiceDictionaryItem
        {
            Name = $"  {longName}  ",
            Description = "  Документы для суда и государственных органов.  ",
            Unit = string.Empty,
            DefaultPrice = -1000m,
            ColorHex = "#0EA5E9",
            IsActive = true,
            CategoryId = category.Id,
        });

        Assert.IsType<CreatedResult>(result);

        db.ChangeTracker.Clear();
        var saved = await db.Services.AsNoTracking().SingleAsync();
        Assert.Equal(longName, saved.Name);
        Assert.Equal("Документы для суда и государственных органов.", saved.Description);
        Assert.Equal("шт", saved.Unit);
        Assert.Equal(0m, saved.DefaultPrice);
        Assert.Equal(category.Id, saved.CategoryId);
    }

    [Fact]
    public async Task CreateService_ReturnsBadRequest_WhenTitleIsTooLong()
    {
        await using var db = await CreateContextAsync();
        var controller = new DictionariesAdminController(db);

        var result = await controller.CreateService(new ServiceDictionaryItem
        {
            Name = new string('а', 1001),
            Description = string.Empty,
            Unit = "шт",
            DefaultPrice = 1000m,
            ColorHex = "#0EA5E9",
            IsActive = true,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Название услуги не должно быть длиннее 1000 символов.", badRequest.Value);
        Assert.Empty(await db.Services.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void Controller_UsesDictionaryRolePermissionInsteadOfAdminPolicy()
    {
        var authorize = Assert.Single(typeof(DictionariesAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        Assert.Null(((AuthorizeAttribute)authorize).Policy);

        var section = Assert.Single(typeof(DictionariesAdminController).GetCustomAttributes(typeof(RequireSectionPermissionAttribute), inherit: true));
        var permission = Assert.IsType<RequireSectionPermissionAttribute>(section);
        Assert.Equal(RolePermissionCatalog.Dictionaries, permission.Section);
        Assert.Equal(RequireSectionPermissionOperations.View, permission.Operation);
    }

    [Fact]
    public void Controller_WriteActionsUseDictionaryOperationPermissions()
    {
        AssertMethodPermission(nameof(DictionariesAdminController.CreateDealType), RequireSectionPermissionOperations.Create);
        AssertMethodPermission(nameof(DictionariesAdminController.UpdateDealType), RequireSectionPermissionOperations.Edit);
        AssertMethodPermission(nameof(DictionariesAdminController.DeleteDealType), RequireSectionPermissionOperations.Delete);
        AssertMethodPermission(nameof(DictionariesAdminController.ToggleDealType), RequireSectionPermissionOperations.Edit);
        AssertMethodPermission(nameof(DictionariesAdminController.MergeSimpleDictionaryDuplicates), RequireSectionPermissionOperations.Edit);
    }

    private static PaymentContext CreateContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        return new PaymentContext(options);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static void AssertMethodPermission(string methodName, string operation)
    {
        var method = typeof(DictionariesAdminController).GetMethod(methodName);
        Assert.NotNull(method);

        var attribute = Assert.Single(method.GetCustomAttributes(typeof(RequireSectionPermissionAttribute), inherit: true));
        var permission = Assert.IsType<RequireSectionPermissionAttribute>(attribute);
        Assert.Equal(RolePermissionCatalog.Dictionaries, permission.Section);
        Assert.Equal(operation, permission.Operation);
    }
}
