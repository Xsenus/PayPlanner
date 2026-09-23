using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CasesControllerTests
{
    [Fact]
    public async Task LegacyCases_ReturnForbidWithoutCaseViewPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "legacy-cases-no-view@example.com", canView: false);
        var clientCase = await CreateCaseAsync(db, user.Id, "Legacy дело без просмотра");
        var controller = CreateController(db, user);

        var listResult = await controller.Get(clientId: null, legalEntityId: null, CancellationToken.None);
        var detailsResult = await controller.GetById(clientCase.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(listResult);
        Assert.IsType<ForbidResult>(detailsResult);
    }

    [Fact]
    public async Task LegacyCases_RequireActionPermissionsForCreateUpdateAndDelete()
    {
        await using var db = await CreateContextAsync();
        var noCreate = await CreateUserAsync(db, "legacy-cases-no-create@example.com", canCreate: false);
        var noEdit = await CreateUserAsync(db, "legacy-cases-no-edit@example.com", canEdit: false);
        var noDelete = await CreateUserAsync(db, "legacy-cases-no-delete@example.com", canDelete: false);
        var noEditCase = await CreateCaseAsync(db, noEdit.Id, "Legacy дело без редактирования");
        var noDeleteCase = await CreateCaseAsync(db, noDelete.Id, "Legacy дело без удаления");
        var noCreateClient = new Client
        {
            Name = "Клиент без создания дела",
            IsActive = true,
            ResponsibleUserId = noCreate.Id,
        };
        db.Clients.Add(noCreateClient);
        await db.SaveChangesAsync();

        var createResult = await CreateController(db, noCreate).Create(new ClientCase
        {
            ClientId = noCreateClient.Id,
            Title = "Запрещенное создание legacy дела",
            Status = ClientCaseStatus.Open,
        }, CancellationToken.None);
        var updateResult = await CreateController(db, noEdit).Update(noEditCase.Id, new ClientCase
        {
            ClientId = noEditCase.ClientId,
            Title = "Запрещенное изменение legacy дела",
            Status = ClientCaseStatus.Closed,
        }, CancellationToken.None);
        var deleteResult = await CreateController(db, noDelete).Delete(noDeleteCase.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(createResult);
        Assert.IsType<ForbidResult>(updateResult);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.False(await db.ClientCases.AnyAsync(item => item.Title == "Запрещенное создание legacy дела"));

        await db.Entry(noEditCase).ReloadAsync();
        Assert.Equal("Legacy дело без редактирования", noEditCase.Title);
        Assert.Equal(ClientCaseStatus.Open, noEditCase.Status);
        Assert.True(await db.ClientCases.AnyAsync(item => item.Id == noDeleteCase.Id));
    }

    [Fact]
    public async Task LegacyCaseEdit_PreservesPositionReviewUntilPositionChanges()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "legacy-position@example.com");
        var clientCase = await CreateCaseAsync(db, lawyer.Id, "Дело с проверенной позицией");
        var reviewedAt = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
        clientCase.Strategy = "Первоначальная позиция";
        clientCase.PositionReviewedAt = reviewedAt;
        clientCase.PositionReviewedByUserId = lawyer.Id;
        clientCase.PositionReviewedByName = "Юрист";
        await db.SaveChangesAsync();
        var controller = CreateController(db, lawyer);

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title,
            Status = ClientCaseStatus.Open, Strategy = clientCase.Strategy,
        }, CancellationToken.None));
        Assert.Equal(reviewedAt, clientCase.PositionReviewedAt);

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title,
            Status = ClientCaseStatus.Open, Strategy = "Изменённая позиция",
        }, CancellationToken.None));
        Assert.Null(clientCase.PositionReviewedAt);
        Assert.Null(clientCase.PositionReviewedByUserId);
        Assert.Equal(string.Empty, clientCase.PositionReviewedByName);
    }

    [Fact]
    public async Task LegacyCaseEdit_ClearsNextHearingConfirmationOnlyWhenDateChanges()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "legacy-hearing@example.com");
        var clientCase = await CreateCaseAsync(db, lawyer.Id, "Дело со сверенной датой");
        var nextHearing = DateTime.UtcNow.AddDays(7);
        clientCase.NextHearingAt = nextHearing;
        clientCase.NextHearingConfirmedForAt = nextHearing;
        clientCase.NextHearingConfirmedAt = DateTime.UtcNow;
        clientCase.NextHearingConfirmedByUserId = lawyer.Id;
        clientCase.NextHearingConfirmedByName = "Юрист";
        await db.SaveChangesAsync();
        var controller = CreateController(db, lawyer);

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            NextHearingAt = nextHearing,
        }, CancellationToken.None));
        Assert.NotNull(clientCase.NextHearingConfirmedAt);

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            NextHearingAt = nextHearing.AddDays(1),
        }, CancellationToken.None));
        Assert.Null(clientCase.NextHearingConfirmedForAt);
        Assert.Null(clientCase.NextHearingConfirmedAt);
        Assert.Null(clientCase.NextHearingConfirmedByUserId);
        Assert.Equal(string.Empty, clientCase.NextHearingConfirmedByName);
    }

    [Fact]
    public async Task LegacyCases_ApplyObjectVisibilityAndDoNotReturnPayments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "legacy-cases-owner@example.com", viewScope: DataVisibilityScope.Own);
        var other = await CreateUserAsync(db, "legacy-cases-other@example.com", viewScope: DataVisibilityScope.Own);
        var visibleCase = await CreateCaseAsync(db, owner.Id, "Видимое legacy дело");
        var hiddenCase = await CreateCaseAsync(db, other.Id, "Скрытое legacy дело");
        db.Payments.Add(new Payment
        {
            ClientCaseId = visibleCase.Id,
            ClientId = visibleCase.ClientId,
            Date = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc),
            Amount = 12345m,
            Type = PaymentType.Income,
            Description = "Платеж не должен попасть в legacy карточку дела",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, owner);

        var listResult = await controller.Get(clientId: null, legalEntityId: null, CancellationToken.None);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCase>>(Assert.IsType<OkObjectResult>(listResult).Value);
        var detailsResult = await controller.GetById(visibleCase.Id, CancellationToken.None);
        var details = Assert.IsType<ClientCase>(Assert.IsType<OkObjectResult>(detailsResult).Value);
        var hiddenDetailsResult = await controller.GetById(hiddenCase.Id, CancellationToken.None);
        var hiddenUpdateResult = await controller.Update(hiddenCase.Id, new ClientCase
        {
            ClientId = hiddenCase.ClientId,
            Title = "Попытка изменить чужое legacy дело",
            Status = ClientCaseStatus.Closed,
        }, CancellationToken.None);
        var hiddenDeleteResult = await controller.Delete(hiddenCase.Id, CancellationToken.None);

        Assert.Collection(items, item => Assert.Equal(visibleCase.Id, item.Id));
        Assert.Empty(details.Payments);
        Assert.IsType<NotFoundResult>(hiddenDetailsResult);
        Assert.IsType<NotFoundResult>(hiddenUpdateResult);
        Assert.IsType<NotFoundResult>(hiddenDeleteResult);
    }

    [Fact]
    public async Task LegacyCases_PublishesJournalChangeAfterSuccessfulCreate()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "legacy-cases-realtime@example.com");
        var client = new Client
        {
            Name = "Клиент realtime legacy дела",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var realtimePublisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(db, owner, realtimePublisher);

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Новое legacy дело",
            Status = ClientCaseStatus.Open,
        }, CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(1, realtimePublisher.TaskJournalChangedCount);
    }

    private static CasesController CreateController(
        PaymentContext db,
        User user,
        IRealtimeNotificationPublisher? realtimePublisher = null)
        => new(db, realtimePublisher)
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

    private sealed class RecordingRealtimeNotificationPublisher : IRealtimeNotificationPublisher
    {
        public int TaskJournalChangedCount { get; private set; }

        public Task PublishUserNotificationAsync(
            UserNotification notification,
            bool toastEnabled = true,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            TaskJournalChangedCount++;
            return Task.CompletedTask;
        }
    }

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
            Name = $"case-legacy-{Guid.NewGuid():N}",
            Description = "Пользователь legacy дел",
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
