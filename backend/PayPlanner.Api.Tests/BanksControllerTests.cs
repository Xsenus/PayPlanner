using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BanksControllerTests
{
    [Fact]
    public async Task CreateUpdateDeleteBank_RoundTripsSuccessfully()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, 51);

        var createAction = await controller.CreateBank(
            new BankUpsertRequest
            {
                Name = "ООО \"Банк Точка\"",
                Bik = "044525104",
                CorrespondentAccount = "30101810745374525104",
                City = "Москва",
                IsForeign = false,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(createAction.Result);
        var createdDto = Assert.IsType<BankDto>(created.Value);
        Assert.Equal("ООО \"Банк Точка\"", createdDto.Name);

        var listAction = await controller.GetBanks(null, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listAction.Result);
        var banks = Assert.IsAssignableFrom<IReadOnlyCollection<BankDto>>(listOk.Value);
        Assert.Single(banks);

        var updateAction = await controller.UpdateBank(
            createdDto.Id,
            new BankUpsertRequest
            {
                Name = "ООО \"Банк Точка\"",
                Bik = "044525104",
                CorrespondentAccount = "30101810745374525104",
                City = "Самара",
                IsForeign = false,
            },
            CancellationToken.None);

        var updatedOk = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<BankDto>(updatedOk.Value);
        Assert.Equal("Самара", updatedDto.City);

        var deleteAction = await controller.DeleteBank(createdDto.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteAction);

        Assert.False(await db.Banks.AnyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteBank_ReturnsConflict_WhenBankUsedInLegalEntityRequisites()
    {
        await using var db = await CreateContextAsync();
        var bank = new Bank
        {
            Name = "Тестовый банк",
            Bik = "123456789",
            CreatedAt = DateTime.UtcNow,
        };

        db.Banks.Add(bank);
        await db.SaveChangesAsync();

        db.LegalEntities.Add(new LegalEntity
        {
            ShortName = "ООО Тест",
            FullName = "ООО Тест",
            CreatedAt = DateTime.UtcNow,
            BankAccountsJson = $"[{{\"bankId\":{bank.Id},\"bank\":\"Тестовый банк\",\"accountNumber\":\"40802810000000000001\"}}]",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, 52);
        var deleteAction = await controller.DeleteBank(bank.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(deleteAction);
        Assert.Equal("Банк используется в реквизитах юридических лиц и не может быть удален.", conflict.Value);
    }

    [Fact]
    public async Task BankEndpoints_RequireDictionaryPermissions()
    {
        await using var db = await CreateContextAsync();
        var bank = new Bank
        {
            Name = "Банк для проверки прав",
            Bik = "123456789",
            CreatedAt = DateTime.UtcNow,
        };
        db.Banks.Add(bank);
        await db.SaveChangesAsync();

        var noViewUser = await CreateDictionaryUserAsync(db, "bank-no-view", canView: false, canCreate: true, canEdit: true, canDelete: true);
        var noCreateUser = await CreateDictionaryUserAsync(db, "bank-no-create", canView: true, canCreate: false, canEdit: true, canDelete: true);
        var noEditUser = await CreateDictionaryUserAsync(db, "bank-no-edit", canView: true, canCreate: true, canEdit: false, canDelete: true);
        var noDeleteUser = await CreateDictionaryUserAsync(db, "bank-no-delete", canView: true, canCreate: true, canEdit: true, canDelete: false);

        var listAction = await CreateController(db, noViewUser.Id, isAdmin: false).GetBanks(null, CancellationToken.None);
        var getAction = await CreateController(db, noViewUser.Id, isAdmin: false).GetBank(bank.Id, CancellationToken.None);
        var createAction = await CreateController(db, noCreateUser.Id, isAdmin: false).CreateBank(BuildBankRequest("Новый банк"), CancellationToken.None);
        var updateAction = await CreateController(db, noEditUser.Id, isAdmin: false).UpdateBank(bank.Id, BuildBankRequest("Обновленный банк"), CancellationToken.None);
        var deleteAction = await CreateController(db, noDeleteUser.Id, isAdmin: false).DeleteBank(bank.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(listAction.Result);
        Assert.IsType<ForbidResult>(getAction.Result);
        Assert.IsType<ForbidResult>(createAction.Result);
        Assert.IsType<ForbidResult>(updateAction.Result);
        Assert.IsType<ForbidResult>(deleteAction);
    }

    [Fact]
    public async Task BankEndpoints_AllowNonAdminRoleWithDictionaryPermissions()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateDictionaryUserAsync(db, "bank-manager", canView: true, canCreate: true, canEdit: true, canDelete: true);
        var controller = CreateController(db, user.Id, isAdmin: false);

        var createAction = await controller.CreateBank(BuildBankRequest("Ролевой банк"), CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(createAction.Result);
        var createdDto = Assert.IsType<BankDto>(created.Value);

        var listAction = await controller.GetBanks(null, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listAction.Result);
        var banks = Assert.IsAssignableFrom<IReadOnlyCollection<BankDto>>(listOk.Value);
        Assert.Single(banks);

        var updateAction = await controller.UpdateBank(createdDto.Id, BuildBankRequest("Ролевой банк", city: "Самара"), CancellationToken.None);
        var updatedOk = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<BankDto>(updatedOk.Value);
        Assert.Equal("Самара", updatedDto.City);

        var deleteAction = await controller.DeleteBank(createdDto.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteAction);
        Assert.False(await db.Banks.AnyAsync(CancellationToken.None));
    }

    private static BankUpsertRequest BuildBankRequest(string name, string city = "Москва")
        => new()
        {
            Name = name,
            Bik = "044525104",
            CorrespondentAccount = "30101810745374525104",
            City = city,
            IsForeign = false,
        };

    private static async Task<User> CreateDictionaryUserAsync(
        PaymentContext db,
        string name,
        bool canView,
        bool canCreate,
        bool canEdit,
        bool canDelete)
    {
        var role = new Role
        {
            Name = $"{name}-{Guid.NewGuid():N}",
            Description = name,
            Permissions =
            {
                new RolePermission
                {
                    Section = RolePermissionCatalog.Dictionaries,
                    CanView = canView,
                    CanCreate = canCreate,
                    CanEdit = canEdit,
                    CanDelete = canDelete,
                    CanExport = true,
                    ViewScope = DataVisibilityScope.All,
                },
            },
        };
        var user = new User
        {
            Email = $"{name}-{Guid.NewGuid():N}@example.test",
            FullName = name,
            PasswordHash = "test",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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

    private static BanksController CreateController(PaymentContext db, int userId, bool isAdmin = true)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый администратор"),
        };

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        var controller = new BanksController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }
}
