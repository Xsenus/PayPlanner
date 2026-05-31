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

public sealed class YandexDiskControllerTests
{
    [Fact]
    public async Task CreateFolder_AllowsRolePermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = true });
        var service = new FakeYandexDiskService
        {
            CreateFolderResult = new YandexDiskOperationResultDto { Success = true, Path = "/Docs" },
        };
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.CreateFolder(new CreateYandexDiskFolderRequest { Path = "/Docs" }, CancellationToken.None);

        var result = Assert.IsType<YandexDiskOperationResultDto>(response.Value);
        Assert.Equal("/Docs", result.Path);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("yandex-disk", audit.ObjectType);
        Assert.Equal("FolderCreated", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("/Docs", audit.MetadataJson);
    }

    [Fact]
    public async Task Delete_ForbidsUserWithoutDeletePermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanDelete = false });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Delete("/Docs", false, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(scope.Db.ObjectAuditEvents);
        Assert.False(service.DeleteCalled);
    }

    [Fact]
    public async Task Copy_AllowsCreatePermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = true });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Copy(new YandexDiskResourceOperationRequest
        {
            FromPath = "/Docs/source.txt",
            ToPath = "/Docs/copy.txt",
            Overwrite = true,
        }, CancellationToken.None);

        var result = Assert.IsType<YandexDiskOperationResultDto>(response.Value);
        Assert.Equal("/Docs/copy.txt", result.Path);
        Assert.True(service.CopyCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("Copied", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("/Docs/source.txt", audit.MetadataJson);
        Assert.Contains("/Docs/copy.txt", audit.MetadataJson);
    }

    [Fact]
    public async Task Copy_ForbidsUserWithoutCreatePermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = false });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Copy(new YandexDiskResourceOperationRequest
        {
            FromPath = "/Docs/source.txt",
            ToPath = "/Docs/copy.txt",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(scope.Db.ObjectAuditEvents);
        Assert.False(service.CopyCalled);
    }

    [Fact]
    public async Task Move_AllowsEditPermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanEdit = true });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Move(new YandexDiskResourceOperationRequest
        {
            FromPath = "/Docs/source.txt",
            ToPath = "/Archive/source.txt",
            Overwrite = false,
        }, CancellationToken.None);

        var result = Assert.IsType<YandexDiskOperationResultDto>(response.Value);
        Assert.Equal("/Archive/source.txt", result.Path);
        Assert.True(service.MoveCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("Moved", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("/Docs/source.txt", audit.MetadataJson);
        Assert.Contains("/Archive/source.txt", audit.MetadataJson);
    }

    [Fact]
    public async Task Move_ForbidsUserWithoutEditPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanEdit = false });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Move(new YandexDiskResourceOperationRequest
        {
            FromPath = "/Docs/source.txt",
            ToPath = "/Archive/source.txt",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(scope.Db.ObjectAuditEvents);
        Assert.False(service.MoveCalled);
    }

    [Fact]
    public async Task Download_RequiresExportPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanExport = false });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.Download("/file.txt", CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        Assert.False(service.DownloadCalled);
    }

    [Fact]
    public async Task Upload_AllowsCreatePermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = true });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));
        using var stream = new MemoryStream([1, 2, 3]);
        var file = new FormFile(stream, 0, stream.Length, "file", "report.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };

        var response = await controller.Upload(file, "/Docs/report.txt", overwrite: true, CancellationToken.None);

        var result = Assert.IsType<YandexDiskOperationResultDto>(response.Value);
        Assert.Equal("/Docs/report.txt", result.Path);
        Assert.True(service.UploadCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("Uploaded", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("/Docs/report.txt", audit.MetadataJson);
        Assert.Contains("report.txt", audit.MetadataJson);
    }

    [Fact]
    public async Task Upload_ForbidsUserWithoutCreatePermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanCreate = false });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));
        using var stream = new MemoryStream([1, 2, 3]);
        var file = new FormFile(stream, 0, stream.Length, "file", "report.txt");

        var response = await controller.Upload(file, "/Docs/report.txt", overwrite: true, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(scope.Db.ObjectAuditEvents);
        Assert.False(service.UploadCalled);
    }

    [Fact]
    public async Task SaveText_AllowsEditPermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanEdit = true });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.SaveText(new UpdateYandexDiskTextFileRequest
        {
            Path = "/Docs/readme.txt",
            Content = "updated",
            Overwrite = true,
        }, CancellationToken.None);

        var result = Assert.IsType<YandexDiskOperationResultDto>(response.Value);
        Assert.Equal("/Docs/readme.txt", result.Path);
        Assert.True(service.SaveTextCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("TextUpdated", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("/Docs/readme.txt", audit.MetadataJson);
        Assert.Contains("contentLength", audit.MetadataJson);
    }

    [Fact]
    public async Task SaveText_ForbidsUserWithoutEditPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanEdit = false });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.SaveText(new UpdateYandexDiskTextFileRequest
        {
            Path = "/Docs/readme.txt",
            Content = "updated",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(scope.Db.ObjectAuditEvents);
        Assert.False(service.SaveTextCalled);
    }

    [Fact]
    public async Task UpdateCrmSyncSettings_AllowsEditPermissionAndWritesAudit()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true, CanEdit = true });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.UpdateCrmSyncSettings(new UpdateYandexDiskCrmSyncSettingsRequest
        {
            Enabled = true,
            SyncInvoices = true,
            SyncActs = false,
            SyncContracts = true,
            SyncCaseDocuments = true,
            SyncTaskFiles = true,
            OverwriteExisting = false,
            ConflictResolutionMode = "manualConfirmation",
            FolderTemplate = "CRM/{clientName}",
            FileNameTemplate = "{documentDate}-{documentTitle}",
        }, CancellationToken.None);

        var result = Assert.IsType<YandexDiskCrmSyncSettingsDto>(response.Value);
        Assert.True(result.Enabled);
        Assert.Equal("manualConfirmation", result.ConflictResolutionMode);
        Assert.Equal("CRM/{clientName}", result.FolderTemplate);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("CrmSyncSettingsUpdated", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("CRM/{clientName}", audit.MetadataJson);
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
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.TestConnection(new TestYandexDiskConnectionRequest { TestFolderPath = "/Check" }, CancellationToken.None);

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
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.TestConnection(new TestYandexDiskConnectionRequest { TestFolderPath = "/Check" }, CancellationToken.None);

        var result = Assert.IsType<YandexDiskConnectionTestDto>(response.Value);
        Assert.True(result.Success);
        Assert.Equal("/Check", result.TestFolderPath);
        Assert.True(service.TestConnectionCalled);
        var audit = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("ConnectionTested", audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Contains("/Check", audit.MetadataJson);
    }

    [Fact]
    public async Task TestConnection_UsesDefaultRequestWhenBodyIsEmpty()
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
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.TestConnection(null, CancellationToken.None);

        var result = Assert.IsType<YandexDiskConnectionTestDto>(response.Value);
        Assert.True(result.Success);
        Assert.Equal("/_payplanner-test", result.TestFolderPath);
        Assert.True(service.TestConnectionCalled);
    }

    [Fact]
    public async Task PreviewCrmSync_AllowsViewPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true });
        var service = new FakeYandexDiskService();
        var controller = CreateController(scope.Db, service, CreateUserPrincipal(user.Id));

        var response = await controller.PreviewCrmSync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var preview = Assert.IsType<YandexDiskCrmSyncPreviewDto>(ok.Value);
        var item = Assert.Single(preview.Items);
        Assert.Equal("invoice", item.DocumentKind);
        Assert.Equal("disk:/PayPlanner/CRM/Счет/example.pdf", item.FullPath);
    }

    [Fact]
    public async Task GetCrmSyncStatus_ReturnsCountsAndRecentItems()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var user = await SeedUserAsync(scope.Db, permission => permission with { CanView = true });
        scope.Db.YandexDiskCrmSyncStates.AddRange(
            new YandexDiskCrmSyncState
            {
                DocumentKind = "contract",
                ObjectType = "contract",
                ObjectId = 1,
                FileName = "contract.pdf",
                YandexDiskPath = "/CRM/contract.pdf",
                Status = YandexDiskCrmSyncStatus.Success,
                AttemptCount = 1,
                LastSyncedAt = now.AddMinutes(-5),
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-5),
            },
            new YandexDiskCrmSyncState
            {
                DocumentKind = "invoice",
                ObjectType = "payment",
                ObjectId = 2,
                FileName = "invoice.pdf",
                Status = YandexDiskCrmSyncStatus.Failed,
                LastError = "Temporary error",
                AttemptCount = 2,
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-2),
            });
        await scope.Db.SaveChangesAsync();
        var controller = CreateController(scope.Db, new FakeYandexDiskService(), CreateUserPrincipal(user.Id));

        var response = await controller.GetCrmSyncStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var report = Assert.IsType<YandexDiskCrmSyncStatusReportDto>(ok.Value);
        Assert.Equal(1, report.SuccessCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(now.AddMinutes(-5), report.LastSuccessfulSyncAt);
        Assert.Equal(2, report.RecentItems.Count);
        Assert.Equal("Ошибка", report.RecentItems[0].StatusText);
    }

    private static YandexDiskController CreateController(PaymentContext db, IYandexDiskService service, ClaimsPrincipal user)
        => new(service, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user },
            },
        };

    private static async Task<User> SeedUserAsync(PaymentContext db, Func<RolePermissionValues, RolePermissionValues> configure)
    {
        var role = new Role { Name = $"role-{Guid.NewGuid():N}", Description = "Yandex Disk role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var defaults = RolePermissionCatalog.BuildDefaults(allowRoleManagement: false);
        foreach (var pair in defaults)
        {
            var values = string.Equals(pair.Key, RolePermissionCatalog.YandexDisk, StringComparison.OrdinalIgnoreCase)
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
            FullName = "Yandex Disk User",
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
                new Claim(ClaimTypes.Name, "Yandex Disk User"),
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

    private sealed class FakeYandexDiskService : IYandexDiskService
    {
        public YandexDiskOperationResultDto CreateFolderResult { get; set; } = new() { Success = true, Path = "/" };
        public bool CopyCalled { get; private set; }
        public bool MoveCalled { get; private set; }
        public bool DeleteCalled { get; private set; }
        public bool UploadCalled { get; private set; }
        public bool DownloadCalled { get; private set; }
        public bool SaveTextCalled { get; private set; }
        public bool TestConnectionCalled { get; private set; }

        public Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = "disk:/PayPlanner" });

        public Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = request.RootPath ?? "disk:/PayPlanner" });

        public Task<YandexDiskConnectionTestDto> TestConnectionAsync(TestYandexDiskConnectionRequest request, CancellationToken cancellationToken = default)
        {
            TestConnectionCalled = true;
            return Task.FromResult(new YandexDiskConnectionTestDto
            {
                Success = true,
                RootPath = "disk:/PayPlanner",
                TestFolderPath = request.TestFolderPath ?? "/_payplanner-test",
                TestFilePath = "/_payplanner-test/payplanner-connection-test.txt",
                CheckedAt = DateTime.UtcNow,
                Steps =
                [
                    new YandexDiskConnectionTestStepDto
                    {
                        Key = "root",
                        Name = "Проверка корневой папки",
                        Success = true,
                        Message = "Корневая папка доступна.",
                    },
                ],
            });
        }

        public Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskListResponseDto());

        public Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateFolderResult);

        public Task<YandexDiskOperationResultDto> CopyAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
        {
            CopyCalled = true;
            return Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = toPath ?? "/" });
        }

        public Task<YandexDiskOperationResultDto> MoveAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
        {
            MoveCalled = true;
            return Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = toPath ?? "/" });
        }

        public Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken cancellationToken = default)
        {
            DeleteCalled = true;
            return Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });
        }

        public Task<YandexDiskOperationResultDto> UploadAsync(string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default)
        {
            UploadCalled = true;
            return Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });
        }

        public Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default)
        {
            DownloadCalled = true;
            return Task.FromResult(new YandexDiskDownload("file.txt", "text/plain", [1, 2, 3]));
        }

        public Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskTextFileDto { Path = path ?? "/", Content = string.Empty });

        public Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken cancellationToken = default)
        {
            SaveTextCalled = true;
            return Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = request.Path ?? "/" });
        }

        public Task<YandexDiskCrmSyncSettingsDto> GetCrmSyncSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskCrmSyncSettingsDto
            {
                Enabled = false,
                SyncInvoices = true,
                SyncActs = true,
                SyncContracts = true,
                SyncCaseDocuments = true,
                SyncTaskFiles = false,
                OverwriteExisting = false,
                ConflictResolutionMode = "yandexDiskMaster",
                ConflictResolutionModeText = "Яндекс.Диск главный",
                FolderTemplate = "CRM/{clientName}/{caseNumber}/{documentType}",
                FileNameTemplate = "{documentDate}-{documentNumber}-{documentTitle}",
                AvailableTokens = ["clientName", "documentTitle"],
            });

        public Task<YandexDiskCrmSyncSettingsDto> UpdateCrmSyncSettingsAsync(UpdateYandexDiskCrmSyncSettingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskCrmSyncSettingsDto
            {
                Enabled = request.Enabled,
                SyncInvoices = request.SyncInvoices,
                SyncActs = request.SyncActs,
                SyncContracts = request.SyncContracts,
                SyncCaseDocuments = request.SyncCaseDocuments,
                SyncTaskFiles = request.SyncTaskFiles,
                OverwriteExisting = request.OverwriteExisting,
                ConflictResolutionMode = request.ConflictResolutionMode ?? "yandexDiskMaster",
                ConflictResolutionModeText = request.ConflictResolutionMode == "manualConfirmation" ? "Ручное подтверждение" : "Яндекс.Диск главный",
                FolderTemplate = request.FolderTemplate ?? string.Empty,
                FileNameTemplate = request.FileNameTemplate ?? string.Empty,
                AvailableTokens = ["clientName", "documentTitle"],
            });

        public Task<YandexDiskCrmSyncPreviewDto> PreviewCrmSyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskCrmSyncPreviewDto
            {
                SyncEnabled = false,
                ConflictResolutionMode = "yandexDiskMaster",
                ConflictResolutionModeText = "Яндекс.Диск главный",
                RootPath = "disk:/PayPlanner",
                Items =
                [
                    new YandexDiskCrmSyncPreviewItemDto
                    {
                        DocumentKind = "invoice",
                        DocumentType = "Счет",
                        Enabled = true,
                        TargetPath = "/CRM/Счет/example.pdf",
                        FullPath = "disk:/PayPlanner/CRM/Счет/example.pdf",
                        Operation = "create",
                        OperationText = "Создать новый файл",
                    },
                ],
            });

        public Task<YandexDiskOperationResultDto> UploadCrmDocumentAsync(YandexDiskCrmDocumentUpload upload, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = $"/CRM/{upload.OriginalFileName}" });
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
