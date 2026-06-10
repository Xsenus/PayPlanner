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
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class S3StorageControllerTests
{
    [Fact]
    public async Task GetOverview_AllowsViewPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.GetOverview(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var overview = Assert.IsType<S3StorageConnectionsOverviewDto>(ok.Value);
        Assert.Single(overview.ProviderPresets);
        Assert.True(service.GetOverviewCalled);
    }

    [Fact]
    public async Task GetOverview_ForbidsUserWithoutViewPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = false });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.GetOverview(CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(service.GetOverviewCalled);
    }

    [Fact]
    public async Task UpsertConnection_AllowsEditPermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanEdit = true });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.UpsertConnection(new UpsertS3StorageConnectionRequest
        {
            Name = "HOSTKEY backups",
            Provider = "hostkey",
            Bucket = "payplanner-backups",
            AccessKeyId = "s3uid_1234567890",
            SecretAccessKey = "secret-1234567890",
        }, CancellationToken.None);

        var result = Assert.IsType<S3StorageConnectionDto>(response.Value);
        Assert.Equal("HOSTKEY backups", result.Name);
        Assert.True(service.UpsertCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("s3-storage", audit.ObjectType);
        Assert.Equal("ConnectionCreated", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("HOSTKEY backups", audit.MetadataJson);
        Assert.DoesNotContain("secret-1234567890", audit.MetadataJson);
    }

    [Fact]
    public async Task TestConnection_RequiresFullFilePermissions()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with
        {
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = true,
        });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.TestConnection("s3-1", new TestS3StorageConnectionRequest(), CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(service.TestConnectionCalled);
        Assert.Empty(scope.Db.ObjectAuditEvents);
    }

    [Fact]
    public async Task TestConnection_AllowsFullFilePermissionsAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with
        {
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
        });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.TestConnection("s3-1", new TestS3StorageConnectionRequest(), CancellationToken.None);

        var result = Assert.IsType<S3StorageConnectionTestDto>(response.Value);
        Assert.True(result.Success);
        Assert.True(service.TestConnectionCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("ConnectionTested", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("payplanner-backups", audit.MetadataJson);
    }

    [Fact]
    public async Task CreateFolder_AllowsCreatePermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = true });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.CreateFolder("s3-1", new CreateS3StorageFolderRequest { Path = "/docs" }, CancellationToken.None);

        var result = Assert.IsType<S3StorageOperationResultDto>(response.Value);
        Assert.Equal("/docs", result.Path);
        Assert.True(service.CreateFolderCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("FolderCreated", audit.Action);
        Assert.Contains("/docs", audit.MetadataJson);
    }

    [Fact]
    public async Task Upload_RejectsMissingFile()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = true });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Upload("s3-1", null, "/docs/readme.txt", overwrite: true, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal("Выберите файл для загрузки.", badRequest.Value);
        Assert.False(service.UploadCalled);
        Assert.Empty(scope.Db.ObjectAuditEvents);
    }

    [Fact]
    public async Task Download_RequiresExportPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanExport = false });
        var service = new FakeS3StorageService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Download("s3-1", "/docs/readme.txt", CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        Assert.False(service.DownloadCalled);
    }

    private static S3StorageController CreateController(PaymentContext db, IS3StorageService service, ClaimsPrincipal user)
        => new(service, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user },
            },
        };

    private static async Task<User> SeedUserAsync(PaymentContext db, Func<RolePermissionValues, RolePermissionValues> configure)
    {
        var role = new Role { Name = $"role-{Guid.NewGuid():N}", Description = "S3 role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var defaults = RolePermissionCatalog.BuildDefaults(allowRoleManagement: false);
        foreach (var pair in defaults)
        {
            var values = string.Equals(pair.Key, RolePermissionCatalog.S3Storage, StringComparison.OrdinalIgnoreCase)
                ? configure(pair.Value)
                : pair.Value;
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                Section = pair.Key,
                CanView = values.CanView,
                CanCreate = values.CanCreate,
                CanEdit = values.CanEdit,
                CanDelete = values.CanDelete,
                CanExport = values.CanExport,
                CanViewAnalytics = values.CanViewAnalytics,
                ViewScope = values.ViewScope,
            });
        }

        var user = new User
        {
            Email = $"user-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "S3 User",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ClaimsPrincipal CreateUserPrincipal(int userId)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "S3 User"),
            },
            "test"));

    private static async Task<TestDbScope> CreateDbScopeAsync()
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

    private sealed class FakeS3StorageService : IS3StorageService
    {
        public bool GetOverviewCalled { get; private set; }
        public bool UpsertCalled { get; private set; }
        public bool TestConnectionCalled { get; private set; }
        public bool CreateFolderCalled { get; private set; }
        public bool UploadCalled { get; private set; }
        public bool DownloadCalled { get; private set; }

        public Task<S3StorageConnectionsOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            GetOverviewCalled = true;
            return Task.FromResult(new S3StorageConnectionsOverviewDto
            {
                ProviderPresets =
                [
                    new S3StorageProviderPresetDto
                    {
                        Key = "hostkey",
                        Name = "HOSTKEY S3",
                        Endpoint = S3StorageService.HostkeyEndpoint,
                        Region = S3StorageService.HostkeyRegion,
                        ForcePathStyle = true,
                    },
                ],
            });
        }

        public Task<S3StorageConnectionDto> UpsertConnectionAsync(UpsertS3StorageConnectionRequest request, CancellationToken cancellationToken = default)
        {
            UpsertCalled = true;
            return Task.FromResult(new S3StorageConnectionDto
            {
                Id = request.Id ?? "s3-1",
                Name = request.Name,
                Provider = request.Provider,
                ProviderName = "HOSTKEY S3",
                Endpoint = S3StorageService.HostkeyEndpoint,
                Region = S3StorageService.HostkeyRegion,
                Bucket = request.Bucket,
                RootPrefix = "payplanner",
                IsEnabled = true,
                IsConfigured = true,
                AccessKeyPreview = "s3ui...7890",
                SecretKeyPreview = "secr...7890",
            });
        }

        public Task DeleteConnectionAsync(string id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<S3StorageConnectionTestDto> TestConnectionAsync(string id, TestS3StorageConnectionRequest request, CancellationToken cancellationToken = default)
        {
            TestConnectionCalled = true;
            return Task.FromResult(new S3StorageConnectionTestDto
            {
                Success = true,
                ConnectionId = id,
                Endpoint = S3StorageService.HostkeyEndpoint,
                Region = S3StorageService.HostkeyRegion,
                Bucket = "payplanner-backups",
                TestKey = "payplanner/_payplanner-test/file.txt",
            });
        }

        public Task<S3StorageListResponseDto> ListObjectsAsync(string id, string? path, int limit, int offset, CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageListResponseDto
            {
                Current = new S3StorageObjectDto { Name = "S3", Path = "/", Type = "folder" },
                Items =
                [
                    new S3StorageObjectDto { Name = "readme.txt", Path = "/docs/readme.txt", Key = "payplanner/docs/readme.txt", Type = "file" },
                ],
                Limit = limit,
                Offset = offset,
                Total = 1,
            });

        public Task<S3StorageOperationResultDto> CreateFolderAsync(string id, CreateS3StorageFolderRequest request, CancellationToken cancellationToken = default)
        {
            CreateFolderCalled = true;
            return Task.FromResult(new S3StorageOperationResultDto
            {
                Success = true,
                Path = request.Path,
                Key = $"payplanner{request.Path}",
            });
        }

        public Task<S3StorageOperationResultDto> UploadAsync(string id, string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default)
        {
            UploadCalled = true;
            return Task.FromResult(new S3StorageOperationResultDto { Success = true, Path = path ?? "/file.txt", Key = $"payplanner/{path}" });
        }

        public Task<S3StorageDownloadResult> DownloadAsync(string id, string? path, CancellationToken cancellationToken = default)
        {
            DownloadCalled = true;
            return Task.FromResult(new S3StorageDownloadResult(
                new MemoryStream("hello"u8.ToArray()),
                "text/plain",
                "readme.txt",
                path ?? "/docs/readme.txt",
                "payplanner/docs/readme.txt"));
        }

        public Task<S3StorageOperationResultDto> DeleteObjectAsync(string id, string? path, bool recursive, CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageOperationResultDto { Success = true, Path = path ?? "/", Key = "payplanner/docs/readme.txt" });

        public Task<S3StorageOperationResultDto> CopyObjectAsync(string id, S3StorageObjectOperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageOperationResultDto { Success = true, Path = request.ToPath, Key = $"payplanner/{request.ToPath}" });

        public Task<S3StorageOperationResultDto> MoveObjectAsync(string id, S3StorageObjectOperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageOperationResultDto { Success = true, Path = request.ToPath, Key = $"payplanner/{request.ToPath}" });

        public Task<S3StorageTextFileDto> GetTextFileAsync(string id, string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageTextFileDto { Path = path ?? "/docs/readme.txt", Content = "hello" });

        public Task<S3StorageOperationResultDto> SaveTextFileAsync(string id, UpdateS3StorageTextFileRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageOperationResultDto { Success = true, Path = request.Path, Key = $"payplanner/{request.Path}" });
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
