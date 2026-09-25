using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CasesV1ControllerTests
{
    [Fact]
    public async Task V1CourtCaseClosure_CreatesFinalReportForConfiguredOwner()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "v1-final-lawyer@example.com");
        var reportOwner = await CreateUserAsync(db, "v1-final-owner@example.com", viewScope: DataVisibilityScope.All);
        var clientCase = await CreateCaseAsync(db, lawyer.Id, "Судебное дело V1");
        clientCase.CourtName = "Суд общей юрисдикции";
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = ClientCaseFinalReportTaskService.AssigneeUserIdSettingKey,
            Value = reportOwner.Id.ToString(),
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db, lawyer).Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId,
            Title = clientCase.Title,
            CourtName = clientCase.CourtName,
            Status = ClientCaseStatus.Closed,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var task = Assert.Single(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync());
        Assert.Equal("new", task.Status);
        Assert.Equal(reportOwner.Id, task.AssigneeUserId);
        Assert.Contains(ClientCaseFinalReportTaskService.TaskTag, task.TagsJson);
    }

    [Fact]
    public async Task V1Cases_ReturnForbidWithoutCaseViewPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "v1-cases-no-view@example.com", canView: false);
        var clientCase = await CreateCaseAsync(db, user.Id, "Дело без права просмотра");
        var controller = CreateController(db, user);

        var listResult = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            sortBy: null,
            sortDir: null,
            ct: CancellationToken.None);
        var detailsResult = await controller.GetById(clientCase.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(listResult);
        Assert.IsType<ForbidResult>(detailsResult);
    }

    [Fact]
    public async Task V1Cases_ApplyObjectVisibilityToReadUpdateAndDelete()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "v1-cases-owner@example.com", viewScope: DataVisibilityScope.Own);
        var other = await CreateUserAsync(db, "v1-cases-other@example.com", viewScope: DataVisibilityScope.Own);
        var visibleCase = await CreateCaseAsync(db, owner.Id, "Видимое V1 дело");
        var hiddenCase = await CreateCaseAsync(db, other.Id, "Скрытое V1 дело");
        var controller = CreateController(db, owner);

        var listResult = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            sortBy: null,
            sortDir: null,
            ct: CancellationToken.None);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCase>>(Assert.IsType<OkObjectResult>(listResult).Value);
        var detailsResult = await controller.GetById(hiddenCase.Id, CancellationToken.None);
        var updateResult = await controller.Update(hiddenCase.Id, new ClientCase
        {
            ClientId = hiddenCase.ClientId,
            Title = "Попытка изменить чужое дело",
            Status = ClientCaseStatus.Closed,
        }, CancellationToken.None);
        var deleteResult = await controller.Delete(hiddenCase.Id, CancellationToken.None);

        Assert.Collection(items, item => Assert.Equal(visibleCase.Id, item.Id));
        Assert.IsType<NotFoundResult>(detailsResult);
        Assert.IsType<NotFoundResult>(updateResult);
        Assert.IsType<NotFoundResult>(deleteResult);

        var hiddenAfterAttempt = await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == hiddenCase.Id);
        Assert.Equal("Скрытое V1 дело", hiddenAfterAttempt.Title);
        Assert.Equal(ClientCaseStatus.Open, hiddenAfterAttempt.Status);
    }

    [Fact]
    public async Task V1Cases_CreateRejectsOwnerOutsideVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "v1-cases-create-owner@example.com", viewScope: DataVisibilityScope.Own);
        var other = await CreateUserAsync(db, "v1-cases-create-other@example.com", viewScope: DataVisibilityScope.Own);
        var otherClient = new Client
        {
            Name = "Чужой клиент V1",
            IsActive = true,
            ResponsibleUserId = other.Id,
        };
        db.Clients.Add(otherClient);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Create(new ClientCase
        {
            ClientId = otherClient.Id,
            Title = "Создание дела на чужого клиента",
            Status = ClientCaseStatus.Open,
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Unknown clientId", badRequest.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await db.ClientCases.AnyAsync(item => item.Title == "Создание дела на чужого клиента"));
    }

    private static CasesV1Controller CreateController(PaymentContext db, User user)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new Claim(ClaimTypes.Name, user.FullName),
                        new Claim(ClaimTypes.Email, user.Email),
                    ], "TestAuth")),
                },
            },
        };

    private static async Task<ClientCase> CreateCaseAsync(PaymentContext db, int responsibleUserId, string title)
    {
        var client = new Client
        {
            Name = $"{title} клиент",
            IsActive = true,
            ResponsibleUserId = responsibleUserId,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = title,
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsibleUserId,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return clientCase;
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string email,
        bool canView = true,
        bool canCreate = true,
        bool canEdit = true,
        bool canDelete = true,
        string viewScope = DataVisibilityScope.Own)
    {
        var role = new Role
        {
            Name = $"case-v1-{Guid.NewGuid():N}",
            Description = "Пользователь V1 дел",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Cases,
                    CanView = canView,
                    CanCreate = canCreate,
                    CanEdit = canEdit,
                    CanDelete = canDelete,
                    CanExport = true,
                    ViewScope = viewScope,
                },
            ],
        };
        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
