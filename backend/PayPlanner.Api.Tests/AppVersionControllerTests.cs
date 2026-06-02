using System.Security.Claims;
using System.Text.Json;
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
    public async Task GetLatest_IgnoresActiveReleaseScheduledForFuture()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "future-release@example.test");
        var controller = CreateController(
            scope.Db,
            user.Id,
            new FixedTimeProvider(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero)),
            Release("2026-05-26-published", releasedAt: new DateTime(2026, 5, 26, 10, 0, 0)),
            Release("2026-05-27-future", releasedAt: new DateTime(2026, 5, 27, 10, 0, 0)));

        var result = await controller.GetLatest(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AppVersionLatestResponse>(ok.Value);
        Assert.Equal("2026-05-26-published", payload.LatestRelease?.ReleaseId);
    }

    [Fact]
    public async Task GetHistory_IgnoresActiveReleaseScheduledForFuture()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "future-history@example.test");
        var controller = CreateController(
            scope.Db,
            user.Id,
            new FixedTimeProvider(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero)),
            Release("2026-05-26-published", releasedAt: new DateTime(2026, 5, 26, 10, 0, 0)),
            Release("2026-05-27-future", releasedAt: new DateTime(2026, 5, 27, 10, 0, 0)));

        var result = await controller.GetHistory(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsAssignableFrom<IReadOnlyCollection<AppReleaseDto>>(ok.Value);
        var release = Assert.Single(payload);
        Assert.Equal("2026-05-26-published", release.ReleaseId);
    }

    [Fact]
    public async Task GetLatest_UsesLocalApplicationTimeForPublishedReleases()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "local-release-time@example.test");
        var localZone = TimeZoneInfo.CreateCustomTimeZone("PayPlanner Test +07", TimeSpan.FromHours(7), "PayPlanner Test +07", "PayPlanner Test +07");
        var controller = CreateController(
            scope.Db,
            user.Id,
            new FixedTimeProvider(new DateTimeOffset(2026, 5, 27, 5, 0, 0, TimeSpan.Zero), localZone),
            Release("2026-05-27-local-published", releasedAt: new DateTime(2026, 5, 27, 11, 30, 0)),
            Release("2026-05-27-local-future", releasedAt: new DateTime(2026, 5, 27, 12, 30, 0)));

        var result = await controller.GetLatest(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AppVersionLatestResponse>(ok.Value);
        Assert.Equal("2026-05-27-local-published", payload.LatestRelease?.ReleaseId);
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
    public async Task CreateRelease_WhenVersionAlreadyExists_ReturnsConflict()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "admin-duplicate-version@example.test");
        var controller = CreateController(scope.Db, user.Id, Release("2026-05-21-existing", version: "1.13.10"));

        var result = await controller.CreateRelease(new SaveAppReleaseRequest(
            "2026-05-22-manual",
            "1.13.10",
            new DateTime(2026, 5, 22),
            "Manual release",
            "Created from settings",
            true,
            [new SaveAppReleaseItemRequest("new", "Manual item")]), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("Version '1.13.10' already exists.", conflict.Value?.ToString());
    }

    [Fact]
    public async Task UpdateRelease_WhenVersionAlreadyExists_ReturnsConflict()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "admin-update-duplicate-version@example.test");
        var controller = CreateController(
            scope.Db,
            user.Id,
            Release("2026-05-21-first", version: "1.13.10"),
            Release("2026-05-22-second", version: "1.13.11"));
        var releaseId = await scope.Db.AppReleases
            .Where(release => release.ReleaseId == "2026-05-21-first")
            .Select(release => release.Id)
            .SingleAsync();

        var result = await controller.UpdateRelease(releaseId, new SaveAppReleaseRequest(
            "2026-05-21-first",
            "1.13.11",
            new DateTime(2026, 5, 21),
            "Manual release",
            "Created from settings",
            true,
            [new SaveAppReleaseItemRequest("new", "Manual item")]), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("Version '1.13.11' already exists.", conflict.Value?.ToString());
    }

    [Fact]
    public async Task DeleteRelease_RemovesReleaseItemsAndSeenMarks()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "delete-release@example.test");
        var release = Release("2026-05-22-delete");
        var controller = CreateController(scope.Db, user.Id, release);
        await controller.MarkSeen(new AppVersionMarkSeenRequest(release.ReleaseId), CancellationToken.None);
        var releaseId = await scope.Db.AppReleases
            .Where(item => item.ReleaseId == release.ReleaseId)
            .Select(item => item.Id)
            .SingleAsync();

        var result = await controller.DeleteRelease(releaseId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await scope.Db.AppReleases.AnyAsync(item => item.Id == releaseId));
        Assert.False(await scope.Db.AppReleaseItems.AnyAsync(item => item.AppReleaseId == releaseId));
        Assert.False(await scope.Db.AppReleaseSeen.AnyAsync(item => item.AppReleaseId == releaseId));
    }

    [Fact]
    public async Task SeedService_WhenReleaseFileHasDuplicateVersions_Throws()
    {
        await using var scope = await CreateScopeAsync();
        var tempRoot = await CreateTempReleaseRootAsync(
            """
            [
              {
                "releaseId": "2026-05-22-first",
                "version": "1.13.1",
                "releasedAt": "2026-05-22T00:00:00",
                "title": "First release",
                "summary": "First",
                "isActive": true,
                "items": [
                  { "type": "improved", "text": "First item" }
                ]
              },
              {
                "releaseId": "2026-05-23-second",
                "version": "1.13.1",
                "releasedAt": "2026-05-23T00:00:00",
                "title": "Second release",
                "summary": "Second",
                "isActive": true,
                "items": [
                  { "type": "improved", "text": "Second item" }
                ]
              }
            ]
            """);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppReleaseSeedService.SyncAsync(scope.Db, tempRoot));
            Assert.Contains("duplicate version '1.13.1'", exception.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SeedService_WhenReleaseFileHasVersionGap_Throws()
    {
        await using var scope = await CreateScopeAsync();
        var tempRoot = await CreateTempReleaseRootAsync(
            """
            [
              {
                "releaseId": "2026-05-22-first",
                "version": "1.13.1",
                "releasedAt": "2026-05-22T00:00:00",
                "title": "First release",
                "summary": "First",
                "isActive": true,
                "items": [
                  { "type": "improved", "text": "First item" }
                ]
              },
              {
                "releaseId": "2026-05-23-second",
                "version": "1.13.3",
                "releasedAt": "2026-05-23T00:00:00",
                "title": "Second release",
                "summary": "Second",
                "isActive": true,
                "items": [
                  { "type": "improved", "text": "Second item" }
                ]
              }
            ]
            """);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppReleaseSeedService.SyncAsync(scope.Db, tempRoot));
            Assert.Contains("continuous", exception.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AgentReleaseFile_HasUniqueContinuousVersionsAndNewestFirst()
    {
        var releaseFilePath = FindReleaseFilePath();
        using var document = JsonDocument.Parse(File.ReadAllText(releaseFilePath));
        var releases = document.RootElement
            .EnumerateArray()
            .Select(item => new ReleaseFileEntry(
                item.GetProperty("releaseId").GetString() ?? string.Empty,
                ParseVersion(item.GetProperty("version").GetString() ?? string.Empty),
                DateTime.Parse(item.GetProperty("releasedAt").GetString() ?? string.Empty)))
            .ToArray();

        var duplicateVersion = releases
            .GroupBy(release => release.Version.ToString())
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        Assert.Null(duplicateVersion);

        for (var index = 1; index < releases.Length; index++)
        {
            Assert.True(
                CompareReleaseFileEntries(releases[index - 1], releases[index]) >= 0,
                $"Release file must be sorted newest first, but '{releases[index - 1].ReleaseId}' is before '{releases[index].ReleaseId}'.");
        }

        var ordered = releases
            .OrderBy(release => release.ReleasedAt)
            .ThenBy(release => release.Version)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(
                IsNextVersion(ordered[index - 1].Version, ordered[index].Version),
                $"Expected version after '{ordered[index - 1].Version}' to be continuous, but found '{ordered[index].Version}' for '{ordered[index].ReleaseId}'.");
        }
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
        => CreateController(db, userId, TimeProvider.System, releases);

    private static AppVersionController CreateController(PaymentContext db, int userId, TimeProvider timeProvider, params AppRelease[] releases)
    {
        foreach (var release in releases)
        {
            if (!db.AppReleases.Any(item => item.ReleaseId == release.ReleaseId))
            {
                db.AppReleases.Add(release);
            }
        }
        db.SaveChanges();

        var controller = new AppVersionController(db, timeProvider);
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

    private static AppRelease Release(string releaseId, DateTime? releasedAt = null, string version = "1.12.0")
        => new()
        {
            ReleaseId = releaseId,
            Version = version,
            ReleasedAt = releasedAt ?? new DateTime(2026, 5, 21),
            Title = "Test release",
            Summary = "Release summary",
            IsActive = true,
            Items =
            [
                new AppReleaseItem { Type = "new", Text = "New feature", SortOrder = 0 },
            ],
        };

    private static async Task<string> CreateTempReleaseRootAsync(string releasesJson)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"payplanner-releases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "AppReleases"));
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "AppReleases", "releases.json"), releasesJson);
        return tempRoot;
    }

    private static string FindReleaseFilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "AppReleases", "releases.json");
            if (File.Exists(path))
            {
                return path;
            }

            path = Path.Combine(directory.FullName, "backend", "AppReleases", "releases.json");
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find backend/AppReleases/releases.json.");
    }

    private static System.Version ParseVersion(string value)
    {
        var version = new System.Version(value);
        Assert.True(version.Build >= 0, $"Release version '{value}' must contain major, minor and patch parts.");
        return version;
    }

    private static int CompareReleaseFileEntries(ReleaseFileEntry left, ReleaseFileEntry right)
    {
        var dateComparison = left.ReleasedAt.CompareTo(right.ReleasedAt);
        return dateComparison != 0 ? dateComparison : left.Version.CompareTo(right.Version);
    }

    private static bool IsNextVersion(System.Version previous, System.Version current)
    {
        if (current.Major == previous.Major && current.Minor == previous.Minor)
        {
            return current.Build == previous.Build + 1;
        }

        if (current.Major == previous.Major && current.Minor == previous.Minor + 1)
        {
            return current.Build == 0;
        }

        return current.Major == previous.Major + 1 && current.Minor == 0 && current.Build == 0;
    }

    private sealed record ReleaseFileEntry(string ReleaseId, System.Version Version, DateTime ReleasedAt);

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

    private sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo? localTimeZone = null) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone ?? TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
