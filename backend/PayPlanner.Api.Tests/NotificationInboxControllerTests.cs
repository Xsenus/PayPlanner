using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class NotificationInboxControllerTests
{
    [Fact]
    public async Task InboxActions_UpdateOnlyCurrentUserNotification()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current@example.com");
        var otherUser = await CreateUserAsync(db, "other@example.com");
        var currentNotification = CreateNotification(currentUser.Id, "task:1:changed");
        var otherNotification = CreateNotification(otherUser.Id, "task:2:changed");
        db.UserNotifications.AddRange(currentNotification, otherNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Single(page.Items);
        Assert.Equal(currentNotification.Id, page.Items[0].Id);

        var readResult = await controller.MarkRead(currentNotification.Id, CancellationToken.None);
        var readDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(readResult.Result).Value);
        Assert.True(readDto.IsRead);

        var archiveResult = await controller.Archive(currentNotification.Id, CancellationToken.None);
        var archivedDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);
        Assert.True(archivedDto.IsArchived);

        var deleteResult = await controller.Delete(currentNotification.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);

        await db.Entry(currentNotification).ReloadAsync();
        await db.Entry(otherNotification).ReloadAsync();
        Assert.NotNull(currentNotification.DeletedAt);
        Assert.Null(otherNotification.DeletedAt);
    }

    [Fact]
    public async Task GetUnreadCount_IgnoresArchivedReadAndDeletedNotifications()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "unread@example.com");
        var unread = CreateNotification(user.Id, "task:1:changed");
        var read = CreateNotification(user.Id, "task:2:changed");
        read.IsRead = true;
        read.ReadAt = DateTime.UtcNow;
        var archived = CreateNotification(user.Id, "task:3:changed");
        archived.IsArchived = true;
        archived.ArchivedAt = DateTime.UtcNow;
        var deleted = CreateNotification(user.Id, "task:4:changed");
        deleted.DeletedAt = DateTime.UtcNow;
        db.UserNotifications.AddRange(
            unread,
            read,
            archived,
            deleted);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var result = await controller.GetUnreadCount(CancellationToken.None);
        var count = Assert.IsType<int>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, count);
    }

    private static UserNotification CreateNotification(int userId, string eventKey)
        => new()
        {
            RecipientUserId = userId,
            Type = UserNotificationTypes.TaskChanged,
            EventKey = eventKey,
            Title = "Изменение по задаче",
            BodyPreview = "Описание события",
            Tone = "blue",
            ObjectType = "task",
            ObjectId = 1,
            TaskJournalItemId = null,
            MetadataJson = "{}",
            CreatedAt = DateTime.UtcNow
        };

    private static NotificationInboxController CreateController(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Test User")
        };

        return new NotificationInboxController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email)
    {
        var role = new Role
        {
            Name = $"role-{Guid.NewGuid():N}",
            Description = "test",
            CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
