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
        public bool DeleteCalled { get; private set; }
        public bool DownloadCalled { get; private set; }

        public Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = "disk:/PayPlanner" });

        public Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = request.RootPath ?? "disk:/PayPlanner" });

        public Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskListResponseDto());

        public Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateFolderResult);

        public Task<YandexDiskOperationResultDto> CopyAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = toPath ?? "/" });

        public Task<YandexDiskOperationResultDto> MoveAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = toPath ?? "/" });

        public Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken cancellationToken = default)
        {
            DeleteCalled = true;
            return Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });
        }

        public Task<YandexDiskOperationResultDto> UploadAsync(string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });

        public Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default)
        {
            DownloadCalled = true;
            return Task.FromResult(new YandexDiskDownload("file.txt", "text/plain", [1, 2, 3]));
        }

        public Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskTextFileDto { Path = path ?? "/", Content = string.Empty });

        public Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = request.Path ?? "/" });

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
