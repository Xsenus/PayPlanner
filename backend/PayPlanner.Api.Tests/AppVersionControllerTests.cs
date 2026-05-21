using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class AppVersionControllerTests
{
    [Fact]
    public async Task GetLatest_WhenUserHasNotSeenRelease_ReturnsUnseen()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "first@example.test");
        var controller = CreateController(scope.Db, user.Id, Release("2026-05-21-accounting-v2"));

        var result = await controller.GetLatest(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AppVersionLatestResponse>(ok.Value);
        Assert.False(payload.SeenByCurrentUser);
        Assert.Equal("2026-05-21-accounting-v2", payload.LatestRelease?.ReleaseId);
    }

    [Fact]
    public async Task MarkSeen_WhenUserClosesRelease_PreventsRepeatedAutoShow()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "seen@example.test");
        var controller = CreateController(scope.Db, user.Id, Release("2026-05-21-accounting-v2"));

        var markResult = await controller.MarkSeen(new AppVersionMarkSeenRequest("2026-05-21-accounting-v2"), CancellationToken.None);
        var latestResult = await controller.GetLatest(CancellationToken.None);

        Assert.IsType<NoContentResult>(markResult);
        var ok = Assert.IsType<OkObjectResult>(latestResult.Result);
        var payload = Assert.IsType<AppVersionLatestResponse>(ok.Value);
        Assert.True(payload.SeenByCurrentUser);
    }

    [Fact]
    public async Task GetLatest_WhenNewReleaseAppears_ReturnsUnseenAgain()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "new-release@example.test");
        scope.Db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = "app.releaseNotes.seenReleaseId",
            Value = "2026-05-20-previous",
        });
        await scope.Db.SaveChangesAsync();

        var controller = CreateController(
            scope.Db,
            user.Id,
            Release("2026-05-20-previous", releasedAt: new DateTime(2026, 5, 20)),
            Release("2026-05-21-accounting-v2", releasedAt: new DateTime(2026, 5, 21)));

        var result = await controller.GetLatest(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AppVersionLatestResponse>(ok.Value);
        Assert.False(payload.SeenByCurrentUser);
        Assert.Equal("2026-05-21-accounting-v2", payload.LatestRelease?.ReleaseId);
    }

    [Fact]
    public async Task MarkSeen_IsStoredPerUser()
    {
        await using var scope = await CreateScopeAsync();
        var firstUser = await CreateUserAsync(scope.Db, "first-user@example.test");
        var secondUser = await CreateUserAsync(scope.Db, "second-user@example.test");
        var release = Release("2026-05-21-accounting-v2");

        var firstController = CreateController(scope.Db, firstUser.Id, release);
        await firstController.MarkSeen(new AppVersionMarkSeenRequest(release.ReleaseId), CancellationToken.None);

        var secondController = CreateController(scope.Db, secondUser.Id, release);
        var result = await secondController.GetLatest(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AppVersionLatestResponse>(ok.Value);
        Assert.False(payload.SeenByCurrentUser);
    }

    [Fact]
    public async Task CreateRelease_AddsManualReleaseToDatabase()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "admin@example.test");
        var controller = CreateController(scope.Db, user.Id);

        var result = await controller.CreateRelease(new SaveAppReleaseRequest(
            "2026-05-22-manual",
            "1.13.0",
            new DateTime(2026, 5, 22),
            "Manual release",
            "Created from settings",
            true,
            [new SaveAppReleaseItemRequest("new", "Manual item")]), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AppReleaseAdminDto>(ok.Value);
        Assert.Equal("manual", payload.Source);
        Assert.Equal("2026-05-22-manual", payload.ReleaseId);
        Assert.True(await scope.Db.AppReleases.AnyAsync(release => release.ReleaseId == "2026-05-22-manual"));
    }

    [Fact]
    public async Task SeedService_SyncsAgentReleaseFileIntoDatabase()
    {
        await using var scope = await CreateScopeAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"payplanner-releases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "AppReleases"));
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "AppReleases", "releases.json"),
            """
            [
              {
                "releaseId": "2026-05-23-agent",
                "version": "1.14.0",
                "releasedAt": "2026-05-23T00:00:00",
                "title": "Agent release",
                "summary": "Seeded by agent",
                "isActive": true,
                "items": [
                  { "type": "improved", "text": "Agent item" }
                ]
              }
            ]
            """);

        try
        {
            await AppReleaseSeedService.SyncAsync(scope.Db, tempRoot);

            var release = await scope.Db.AppReleases
                .Include(item => item.Items)
                .SingleAsync(item => item.ReleaseId == "2026-05-23-agent");
            Assert.Equal("agent", release.Source);
            Assert.Equal("Agent item", Assert.Single(release.Items).Text);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SeedService_RemovesAgentReleaseMissingFromFile()
    {
        await using var scope = await CreateScopeAsync();
        var stale = Release("2026-05-21-accounting-v2");
        stale.Source = "agent";
        var manual = Release("2026-05-22-manual");
        manual.Source = "manual";
        scope.Db.AppReleases.AddRange(stale, manual);
        await scope.Db.SaveChangesAsync();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"payplanner-releases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "AppReleases"));
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "AppReleases", "releases.json"),
            """
            [
              {
                "releaseId": "2026-05-23-agent",
                "version": "1.14.0",
                "releasedAt": "2026-05-23T00:00:00",
                "title": "Agent release",
                "summary": "Seeded by agent",
                "isActive": true,
                "items": [
                  { "type": "improved", "text": "Agent item" }
                ]
              }
            ]
            """);

        try
        {
            await AppReleaseSeedService.SyncAsync(scope.Db, tempRoot);

            Assert.False(await scope.Db.AppReleases.AnyAsync(release => release.ReleaseId == "2026-05-21-accounting-v2"));
            Assert.True(await scope.Db.AppReleases.AnyAsync(release => release.ReleaseId == "2026-05-22-manual"));
            Assert.True(await scope.Db.AppReleases.AnyAsync(release => release.ReleaseId == "2026-05-23-agent"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static AppVersionController CreateController(PaymentContext db, int userId, params AppRelease[] releases)
    {
        foreach (var release in releases)
        {
            if (!db.AppReleases.Any(item => item.ReleaseId == release.ReleaseId))
            {
                db.AppReleases.Add(release);
            }
        }
        db.SaveChanges();

        var controller = new AppVersionController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "Test User"),
                ], "Test")),
            },
        };
        return controller;
    }

    private static AppRelease Release(string releaseId, DateTime? releasedAt = null)
        => new()
        {
            ReleaseId = releaseId,
            Version = "1.12.0",
            ReleasedAt = releasedAt ?? new DateTime(2026, 5, 21),
            Title = "Test release",
            Summary = "Release summary",
            IsActive = true,
            Items =
            [
                new AppReleaseItem { Type = "new", Text = "New feature", SortOrder = 0 },
            ],
        };

    private static async Task<User> CreateUserAsync(PaymentContext db, string email)
    {
        var role = new Role { Name = $"role-{Guid.NewGuid():N}", Description = "Test role" };
        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            Role = role,
            IsApproved = true,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<TestDbScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();
        return new TestDbScope(connection, db);
    }

    private sealed class TestDbScope(SqliteConnection connection, PaymentContext db) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
