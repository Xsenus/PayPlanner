using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class YandexDiskServiceTests
{
    [Fact]
    public async Task UpdateSettings_NormalizesRootPathAndDoesNotReturnToken()
    {
        await using var scope = await CreateDbScopeAsync();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ => JsonResponse("{}"))));

        var settings = await service.UpdateSettingsAsync(new UpdateYandexDiskSettingsRequest
        {
            OAuthToken = "secret-token-123456",
            RootPath = "/PayPlanner/Test/",
        }, CancellationToken.None);

        Assert.True(settings.IsConfigured);
        Assert.Equal("disk:/PayPlanner/Test", settings.RootPath);
        Assert.Equal("secr...3456", settings.TokenPreview);
        Assert.DoesNotContain("secret-token", settings.TokenPreview);

        var storedToken = await scope.Db.SystemSettings.SingleAsync(item => item.Key == "integrations.yandexDisk.oauthToken");
        Assert.Equal("secret-token-123456", storedToken.Value);
    }

    [Fact]
    public async Task ListAsync_UsesOAuthHeaderAndMapsRelativePaths()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        HttpRequestMessage? captured = null;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            captured = request;
            return JsonResponse("""
            {
              "name": "Docs",
              "path": "disk:/PayPlanner/Docs",
              "type": "dir",
              "_embedded": {
                "limit": 100,
                "offset": 0,
                "total": 1,
                "items": [
                  {
                    "name": "contract.txt",
                    "path": "disk:/PayPlanner/Docs/contract.txt",
                    "type": "file",
                    "mime_type": "text/plain",
                    "size": 12,
                    "modified": "2026-06-01T03:00:00Z"
                  }
                ]
              }
            }
            """);
        })));

        var result = await service.ListAsync("/Docs", 100, 0, CancellationToken.None);

        Assert.Equal("/Docs", result.Current.Path);
        var item = Assert.Single(result.Items);
        Assert.Equal("/Docs/contract.txt", item.Path);
        Assert.Equal("text/plain", item.MimeType);
        Assert.Equal("OAuth", captured?.Headers.Authorization?.Scheme);
        Assert.Equal("ya-token", captured?.Headers.Authorization?.Parameter);
        Assert.Contains("path=disk%3A%2FPayPlanner%2FDocs", captured?.RequestUri?.Query);
    }

    [Fact]
    public async Task CreateFolder_RejectsParentTraversal()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ => JsonResponse("{}"))));

        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(() =>
            service.CreateFolderAsync("../Other", CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData(401, """{"message":"Unauthorized"}""", "OAuth-токен")]
    [InlineData(403, """{"message":"Forbidden"}""", "нет прав")]
    [InlineData(507, """{"message":"Insufficient Storage"}""", "не хватает свободного места")]
    [InlineData(503, """{"message":"Service unavailable"}""", "временно недоступен")]
    public async Task ListAsync_ReturnsHelpfulMessageForCommonYandexDiskErrors(int statusCode, string responseBody, string expectedMessagePart)
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(() =>
            service.ListAsync("/", 100, 0, CancellationToken.None));

        Assert.Equal(statusCode, error.StatusCode);
        Assert.Contains(expectedMessagePart, error.Message);
    }

    [Fact]
    public async Task DownloadAsync_RequestsDownloadLinkAndLoadsFile()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            if (request.RequestUri?.Host == "download.local")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("hello", Encoding.UTF8, "text/plain"),
                };
            }

            return JsonResponse("""{ "href": "https://download.local/file.txt" }""");
        })));

        var file = await service.DownloadAsync("/file.txt", CancellationToken.None);

        Assert.Equal("file.txt", file.FileName);
        Assert.Equal("hello", Encoding.UTF8.GetString(file.Content));
        Assert.StartsWith("text/plain", file.ContentType);
    }

    [Fact]
    public async Task UpdateCrmSyncSettings_NormalizesTemplatesAndPersists()
    {
        await using var scope = await CreateDbScopeAsync();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ => JsonResponse("{}"))));

        var settings = await service.UpdateCrmSyncSettingsAsync(new UpdateYandexDiskCrmSyncSettingsRequest
        {
            Enabled = true,
            SyncInvoices = true,
            SyncActs = false,
            SyncContracts = true,
            SyncCaseDocuments = true,
            SyncTaskFiles = true,
            OverwriteExisting = true,
            ConflictResolutionMode = "manualConfirmation",
            FolderTemplate = "/CRM/../{clientName}/{caseNumber}/",
            FileNameTemplate = " ../{documentDate}-{documentTitle} ",
        }, CancellationToken.None);

        Assert.True(settings.Enabled);
        Assert.False(settings.SyncActs);
        Assert.True(settings.SyncTaskFiles);
        Assert.False(settings.OverwriteExisting);
        Assert.Equal("manualConfirmation", settings.ConflictResolutionMode);
        Assert.Equal("Ручное подтверждение", settings.ConflictResolutionModeText);
        Assert.Equal("CRM/{clientName}/{caseNumber}", settings.FolderTemplate);
        Assert.Equal("{documentDate}-{documentTitle}", settings.FileNameTemplate);
        Assert.Contains("clientName", settings.AvailableTokens);

        var stored = await scope.Db.SystemSettings.SingleAsync(item => item.Key == "integrations.yandexDisk.crmSync.settings");
        Assert.Contains("CRM/{clientName}/{caseNumber}", stored.Value);
        Assert.DoesNotContain("..", stored.Value);
    }

    [Fact]
    public async Task UploadCrmDocumentAsync_BuildsPathFromSettingsAndCreatesFolders()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var requests = new List<string>();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            if (request.RequestUri?.Host == "upload.local")
            {
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/upload", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://upload.local/file" }""");
            }

            return JsonResponse("{}");
        })));

        await service.UpdateCrmSyncSettingsAsync(new UpdateYandexDiskCrmSyncSettingsRequest
        {
            Enabled = true,
            SyncInvoices = false,
            SyncActs = false,
            SyncContracts = false,
            SyncCaseDocuments = true,
            SyncTaskFiles = false,
            OverwriteExisting = true,
            ConflictResolutionMode = "crmMaster",
            FolderTemplate = "CRM/{clientName}/{caseNumber}/{documentType}",
            FileNameTemplate = "{documentDate}-{documentTitle}",
        }, CancellationToken.None);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("document"));
        var result = await service.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
            stream,
            "text/plain",
            "source.txt",
            "caseDocument",
            "Заявление",
            "claim",
            new DateTime(2026, 6, 1),
            "Иск к должнику",
            "Иванов",
            null,
            "CASE-7",
            "Банкротство"),
            CancellationToken.None);

        Assert.Equal("/CRM/Иванов/CASE-7/Заявление/2026-06-01-Иск к должнику.txt", result.Path);
        Assert.Contains(requests, item => item.Contains("/resources?path=disk%3A%2FPayPlanner%2FCRM", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/resources/upload", StringComparison.Ordinal) && item.Contains("overwrite=true", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("PUT https://upload.local/file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewCrmSyncAsync_BuildsDryRunPathsWithoutNetworkCalls()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();
        var requestCount = 0;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ =>
        {
            requestCount++;
            return JsonResponse("{}");
        })));

        await service.UpdateCrmSyncSettingsAsync(new UpdateYandexDiskCrmSyncSettingsRequest
        {
            Enabled = true,
            SyncInvoices = true,
            SyncActs = false,
            SyncContracts = true,
            SyncCaseDocuments = true,
            SyncTaskFiles = false,
            OverwriteExisting = true,
            ConflictResolutionMode = "crmMaster",
            FolderTemplate = "CRM/{clientName}/{caseNumber}/{documentType}",
            FileNameTemplate = "{documentDate}-{documentNumber}-{documentTitle}",
        }, CancellationToken.None);

        var preview = await service.PreviewCrmSyncAsync(CancellationToken.None);

        Assert.Equal(0, requestCount);
        Assert.True(preview.SyncEnabled);
        Assert.True(preview.OverwriteExisting);
        Assert.Equal("crmMaster", preview.ConflictResolutionMode);
        Assert.Equal("disk:/PayPlanner", preview.RootPath);
        Assert.Equal(5, preview.Items.Count);
        var invoice = Assert.Single(preview.Items, item => item.DocumentKind == "invoice");
        Assert.True(invoice.Enabled);
        Assert.Equal("create-or-update", invoice.Operation);
        Assert.Contains("/CRM/ООО Клиент/А40-12345/2026/Счет/", invoice.TargetPath);
        var taskFile = Assert.Single(preview.Items, item => item.DocumentKind == "taskFile");
        Assert.False(taskFile.Enabled);
    }

    [Fact]
    public async Task UploadCrmDocumentAsync_UsesConflictRuleWhenFileAlreadyExists()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var uploadLinkQueries = new List<string>();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/upload", StringComparison.OrdinalIgnoreCase) == true)
            {
                uploadLinkQueries.Add(request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"message":"already exists"}""", Encoding.UTF8, "application/json"),
                };
            }

            return JsonResponse("{}");
        })));

        await service.UpdateCrmSyncSettingsAsync(new UpdateYandexDiskCrmSyncSettingsRequest
        {
            Enabled = true,
            SyncInvoices = true,
            SyncActs = false,
            SyncContracts = false,
            SyncCaseDocuments = false,
            SyncTaskFiles = false,
            OverwriteExisting = false,
            ConflictResolutionMode = "yandexDiskMaster",
            FolderTemplate = "CRM/{documentType}",
            FileNameTemplate = "{documentNumber}",
        }, CancellationToken.None);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("invoice"));
        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(() => service.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
            stream,
            "application/pdf",
            "invoice.pdf",
            "invoice",
            "РЎС‡РµС‚",
            "INV-1",
            new DateTime(2026, 6, 1),
            "РЎС‡РµС‚",
            "РљР»РёРµРЅС‚",
            null,
            null,
            null),
            CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("Яндекс.Диск главный", error.Message);
        Assert.Contains(uploadLinkQueries, query => query.Contains("overwrite=false", StringComparison.Ordinal));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

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

    private sealed class FakeYandexDiskHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
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
