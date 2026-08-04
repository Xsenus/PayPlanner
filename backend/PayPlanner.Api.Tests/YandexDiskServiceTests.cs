using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class YandexDiskServiceTests
{
    private static readonly IDataProtectionProvider TestDataProtectionProvider =
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pp-yandex-disk-tests")));

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
        Assert.StartsWith("protected:v1:", storedToken.Value);
        Assert.DoesNotContain("secret-token-123456", storedToken.Value);
    }

    [Fact]
    public async Task OAuthFlow_UsesSharedYandexApplicationAndStoresProtectedDiskToken()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = "auth.yandex.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.yandex.clientId", Value = "shared-client", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.yandex.clientSecret", Value = "shared-secret", CreatedAt = now, UpdatedAt = now });
        await scope.Db.SaveChangesAsync();

        var service = new YandexDiskService(
            scope.Db,
            new HttpClient(new FakeYandexDiskHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri.StartsWith("https://oauth.yandex.ru/token", StringComparison.Ordinal) == true)
                {
                    return JsonResponse("""{"access_token":"oauth-disk-token","token_type":"bearer"}""");
                }

                Assert.Equal("OAuth", request.Headers.Authorization?.Scheme);
                Assert.Equal("oauth-disk-token", request.Headers.Authorization?.Parameter);
                return JsonResponse("""{"user":{"login":"company"}}""");
            })),
            new ConfigurationBuilder().Build(),
            TestDataProtectionProvider);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("crm.elitpravo.com");

        var started = await service.StartOAuthAsync(7, new StartYandexDiskAuthRequest
        {
            Name = "Рабочий диск",
            RootPath = "disk:/PayPlanner",
            ReturnUrl = "/?ppTab=settings&settingsSection=yandex-disk",
        }, httpContext.Request, CancellationToken.None);

        Assert.Contains("client_id=shared-client", started.AuthUrl);
        Assert.Contains("cloud_api%3Adisk.read", started.AuthUrl);
        Assert.Contains(Uri.EscapeDataString("https://crm.elitpravo.com/api/yandex-disk/auth/callback"), started.AuthUrl);
        var oauthQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(started.AuthUrl).Query);
        var deviceId = Assert.Single(oauthQuery["device_id"]);
        Assert.NotNull(deviceId);
        Assert.StartsWith("pp-yd-", deviceId);
        Assert.InRange(deviceId.Length, 6, 50);

        var html = await service.HandleOAuthCallbackAsync(started.State, "confirmation-code", null, null, httpContext.Request, CancellationToken.None);
        Assert.Contains("Яндекс.Диск подключён", html);

        var status = await service.GetOAuthStatusAsync(7, started.State, CancellationToken.None);
        Assert.True(status.Completed);
        Assert.Equal("completed", status.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.ConnectionId));

        var overview = await service.GetConnectionsOverviewAsync(CancellationToken.None);
        var connection = Assert.Single(overview.Connections);
        Assert.True(connection.IsConfigured);
        Assert.Equal("Рабочий диск", connection.Name);

        var storedConnections = await scope.Db.SystemSettings.SingleAsync(item => item.Key == "integrations.yandexDisk.connections");
        Assert.Contains("protected:v1:", storedConnections.Value);
        Assert.DoesNotContain("oauth-disk-token", storedConnections.Value);
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
    public async Task ListAsync_CreatesMissingRootFolderAndRetries()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var requests = new List<string>();
        var rootReads = 0;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            if (request.Method == HttpMethod.Get && ++rootReads == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"message":"Not Found","error":"DiskNotFoundError"}""", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return JsonResponse("""{ "name": "PayPlanner", "path": "disk:/PayPlanner", "type": "dir", "_embedded": { "limit": 100, "offset": 0, "total": 0, "items": [] } }""");
        })));

        var result = await service.ListAsync(null, 100, 0, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(2, rootReads);
        Assert.Contains(requests, item => item.StartsWith("PUT https://cloud-api.yandex.net/v1/disk/resources?path=disk%3A%2FPayPlanner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectionEndpoints_SaveSeveralYandexDiskAccountsAndUseSelectedOneForFiles()
    {
        await using var scope = await CreateDbScopeAsync();
        HttpRequestMessage? captured = null;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            captured = request;
            return JsonResponse("""
            {
              "name": "Reports",
              "path": "disk:/Archive/Reports",
              "type": "dir",
              "_embedded": {
                "limit": 100,
                "offset": 0,
                "total": 0,
                "items": []
              }
            }
            """);
        })));

        var main = await service.UpsertConnectionAsync(new UpsertYandexDiskConnectionRequest
        {
            Name = "Я.Диск основной",
            OAuthToken = "main-token",
            RootPath = "disk:/PayPlanner",
            IsEnabled = true,
        }, CancellationToken.None);
        var archive = await service.UpsertConnectionAsync(new UpsertYandexDiskConnectionRequest
        {
            Name = "Я.Диск архив",
            OAuthToken = "archive-token",
            RootPath = "/Archive",
            IsEnabled = true,
        }, CancellationToken.None);

        var overview = await service.GetConnectionsOverviewAsync(CancellationToken.None);
        Assert.Equal(2, overview.Connections.Count);
        Assert.Contains(overview.Connections, item => item.Id == main.Id && item.TokenPreview == "main...oken");
        Assert.Contains(overview.Connections, item => item.Id == archive.Id && item.RootPath == "disk:/Archive");

        var result = await service.ListAsync(archive.Id, "/Reports", 100, 0, CancellationToken.None);

        Assert.Equal("/Reports", result.Current.Path);
        Assert.Equal("OAuth", captured?.Headers.Authorization?.Scheme);
        Assert.Equal("archive-token", captured?.Headers.Authorization?.Parameter);
        Assert.Contains("path=disk%3A%2FArchive%2FReports", captured?.RequestUri?.Query);
        Assert.NotNull(await scope.Db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "integrations.yandexDisk.connections"));
        var legacyToken = (await scope.Db.SystemSettings.SingleAsync(item => item.Key == "integrations.yandexDisk.oauthToken")).Value;
        Assert.StartsWith("protected:v1:", legacyToken);
        Assert.DoesNotContain("main-token", legacyToken);
    }

    [Fact]
    public async Task ProtectedTokenFromAnotherKeyRing_KeepsSettingsReadableAndRequiresReconnectWithoutLosingToken()
    {
        await using var scope = await CreateDbScopeAsync();
        var httpClient = new HttpClient(new FakeYandexDiskHandler(_ => JsonResponse("{}")));
        var writer = new YandexDiskService(
            scope.Db,
            httpClient,
            new ConfigurationBuilder().Build(),
            new EphemeralDataProtectionProvider());
        var created = await writer.UpsertConnectionAsync(new UpsertYandexDiskConnectionRequest
        {
            Name = "Архивный диск",
            OAuthToken = "production-only-token",
            RootPath = "disk:/Archive",
            IsEnabled = true,
        }, CancellationToken.None);
        var storedSetting = await scope.Db.SystemSettings.SingleAsync(item => item.Key == "integrations.yandexDisk.connections");
        var originalProtectedToken = ReadFirstConnectionToken(storedSetting.Value);

        var reader = new YandexDiskService(
            scope.Db,
            httpClient,
            new ConfigurationBuilder().Build(),
            new EphemeralDataProtectionProvider());

        var overview = await reader.GetConnectionsOverviewAsync(CancellationToken.None);
        var connection = Assert.Single(overview.Connections);
        Assert.Equal(created.Id, connection.Id);
        Assert.Equal("disk:/Archive", connection.RootPath);
        Assert.False(connection.IsConfigured);
        Assert.True(connection.RequiresReconnect);
        Assert.Null(connection.TokenPreview);

        var settings = await reader.GetSettingsAsync(CancellationToken.None);
        Assert.False(settings.IsConfigured);
        Assert.True(settings.RequiresReconnect);
        Assert.Equal("disk:/Archive", settings.RootPath);

        var preview = await reader.PreviewCrmSyncAsync(CancellationToken.None);
        Assert.Equal("disk:/Archive", preview.RootPath);
        Assert.NotEmpty(preview.Items);

        var operationError = await Assert.ThrowsAsync<YandexDiskOperationException>(() =>
            reader.ListAsync(created.Id, null, 100, 0, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, operationError.StatusCode);
        Assert.Contains("Переподключите", operationError.Message);

        await reader.UpsertConnectionAsync(new UpsertYandexDiskConnectionRequest
        {
            Id = created.Id,
            Name = "Архивный диск (нужно переподключить)",
            RootPath = "disk:/Archive",
            IsEnabled = true,
        }, CancellationToken.None);

        await scope.Db.Entry(storedSetting).ReloadAsync();
        Assert.Equal(originalProtectedToken, ReadFirstConnectionToken(storedSetting.Value));
        Assert.DoesNotContain("production-only-token", storedSetting.Value);
    }

    [Fact]
    public async Task TestConnectionAsync_CreatesUploadsReadsAndDeletesMarkerFile()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var requests = new List<string>();
        string? uploadedText = null;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            if (request.RequestUri?.Host == "upload.local")
            {
                uploadedText = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (request.RequestUri?.Host == "download.local")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(uploadedText ?? string.Empty, Encoding.UTF8, "text/plain"),
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/upload", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://upload.local/marker.txt" }""");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/download", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://download.local/marker.txt" }""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return JsonResponse("{}");
            }

            if (request.Method == HttpMethod.Put)
            {
                return JsonResponse("{}");
            }

            return JsonResponse("""{ "name": "PayPlanner", "path": "disk:/PayPlanner", "type": "dir" }""");
        })));

        var result = await service.TestConnectionAsync(new TestYandexDiskConnectionRequest
        {
            TestFolderPath = "/Acceptance",
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("disk:/PayPlanner", result.RootPath);
        Assert.Equal("/Acceptance", result.TestFolderPath);
        Assert.Equal(5, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.True(step.Success));
        Assert.Contains(requests, item => item.StartsWith("GET https://cloud-api.yandex.net/v1/disk/resources?path=disk%3A%2FPayPlanner", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("PUT https://cloud-api.yandex.net/v1/disk/resources?path=disk%3A%2FPayPlanner%2FAcceptance", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/resources/upload", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("PUT https://upload.local/marker.txt", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/resources/download", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("GET https://download.local/marker.txt", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("DELETE https://cloud-api.yandex.net/v1/disk/resources?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_UsesDefaultFolderWhenRequestHasNoPath()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var requests = new List<string>();
        string? uploadedText = null;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            if (request.RequestUri?.Host == "upload.local")
            {
                uploadedText = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (request.RequestUri?.Host == "download.local")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(uploadedText ?? string.Empty, Encoding.UTF8, "text/plain"),
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/upload", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://upload.local/marker.txt" }""");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/download", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://download.local/marker.txt" }""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return JsonResponse("{}");
            }

            if (request.Method == HttpMethod.Put)
            {
                return JsonResponse("{}");
            }

            return JsonResponse("""{ "name": "PayPlanner", "path": "disk:/PayPlanner", "type": "dir" }""");
        })));

        var result = await service.TestConnectionAsync(new TestYandexDiskConnectionRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("/_payplanner-test", result.TestFolderPath);
        Assert.Equal(5, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.True(step.Success));
        Assert.Contains(requests, item => item.StartsWith("PUT https://cloud-api.yandex.net/v1/disk/resources?path=disk%3A%2FPayPlanner%2F_payplanner-test", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/resources/upload", StringComparison.Ordinal) && item.Contains("%2F_payplanner-test%2F", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("DELETE https://cloud-api.yandex.net/v1/disk/resources?", StringComparison.Ordinal) && item.Contains("%2F_payplanner-test%2F", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_PreservesReadFailureWhenCleanupAlsoFails()
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

            if (request.RequestUri?.Host == "download.local")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("unexpected marker", Encoding.UTF8, "text/plain"),
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/upload", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://upload.local/marker.txt" }""");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/download", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://download.local/marker.txt" }""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"message":"cleanup failed"}""", Encoding.UTF8, "application/json"),
                };
            }

            return JsonResponse("""{ "name": "PayPlanner", "path": "disk:/PayPlanner", "type": "dir" }""");
        })));

        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(() =>
            service.TestConnectionAsync(new TestYandexDiskConnectionRequest { TestFolderPath = "/Acceptance" }, CancellationToken.None));

        Assert.Equal(502, error.StatusCode);
        Assert.Contains(requests, item => item.StartsWith("DELETE https://cloud-api.yandex.net/v1/disk/resources?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_FailsWhenCleanupAfterSuccessfulReadFails()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = "disk:/PayPlanner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();

        var requests = new List<string>();
        string? uploadedText = null;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            if (request.RequestUri?.Host == "upload.local")
            {
                uploadedText = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (request.RequestUri?.Host == "download.local")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(uploadedText ?? string.Empty, Encoding.UTF8, "text/plain"),
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/upload", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://upload.local/marker.txt" }""");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/resources/download", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("""{ "href": "https://download.local/marker.txt" }""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"message":"delete forbidden"}""", Encoding.UTF8, "application/json"),
                };
            }

            return JsonResponse("""{ "name": "PayPlanner", "path": "disk:/PayPlanner", "type": "dir" }""");
        })));

        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(() =>
            service.TestConnectionAsync(new TestYandexDiskConnectionRequest { TestFolderPath = "/Acceptance" }, CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Contains("delete forbidden", error.Message);
        Assert.Contains(requests, item => item.StartsWith("DELETE https://cloud-api.yandex.net/v1/disk/resources?", StringComparison.Ordinal));
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
    [InlineData("delete", "../Other")]
    [InlineData("download", "../Other.txt")]
    [InlineData("upload", "../Other.txt")]
    [InlineData("saveText", "../Other.txt")]
    [InlineData("copySource", "../Source.txt")]
    [InlineData("copyTarget", "../Target.txt")]
    [InlineData("moveSource", "../Source.txt")]
    [InlineData("moveTarget", "../Target.txt")]
    public async Task FileOperations_RejectParentTraversalBeforeNetworkCall(string operation, string path)
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();
        var requestCount = 0;
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ =>
        {
            requestCount++;
            return JsonResponse("{}");
        })));

        async Task ActAsync()
        {
            switch (operation)
            {
                case "delete":
                    await service.DeleteAsync(path, permanently: true, CancellationToken.None);
                    break;
                case "download":
                    await service.DownloadAsync(path, CancellationToken.None);
                    break;
                case "upload":
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("file")))
                    {
                        await service.UploadAsync(path, stream, "text/plain", overwrite: false, CancellationToken.None);
                    }
                    break;
                case "saveText":
                    await service.SaveTextFileAsync(new UpdateYandexDiskTextFileRequest { Path = path, Content = "text" }, CancellationToken.None);
                    break;
                case "copySource":
                    await service.CopyAsync(path, "/Target.txt", overwrite: false, CancellationToken.None);
                    break;
                case "copyTarget":
                    await service.CopyAsync("/Source.txt", path, overwrite: false, CancellationToken.None);
                    break;
                case "moveSource":
                    await service.MoveAsync(path, "/Target.txt", overwrite: false, CancellationToken.None);
                    break;
                case "moveTarget":
                    await service.MoveAsync("/Source.txt", path, overwrite: false, CancellationToken.None);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation {operation}.");
            }
        }

        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(ActAsync);

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Путь не должен содержать переходы", error.Message);
        Assert.Equal(0, requestCount);
    }

    [Theory]
    [InlineData(401, """{"message":"Unauthorized"}""", "OAuth-токен")]
    [InlineData(403, """{"message":"Forbidden"}""", "нет прав")]
    [InlineData(404, """{"message":"Not found"}""", "Not found")]
    [InlineData(429, """{"message":"Too many requests"}""", "Too many requests")]
    [InlineData(500, """{"message":"Internal server error"}""", "Internal server error")]
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
    public async Task ListAsync_ReturnsGatewayTimeoutForExternalTimeout()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = "ya-token", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await scope.Db.SaveChangesAsync();
        var service = new YandexDiskService(scope.Db, new HttpClient(new FakeYandexDiskHandler(_ =>
            throw new TaskCanceledException("Yandex Disk timeout."))));

        var error = await Assert.ThrowsAsync<YandexDiskOperationException>(() =>
            service.ListAsync("/", 100, 0, CancellationToken.None));

        Assert.Equal(504, error.StatusCode);
        Assert.Contains("не ответил", error.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task UploadAsync_CreatesNestedParentFoldersBeforeUploadingBackup()
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
                return JsonResponse("""{ "href": "https://upload.local/backup" }""");
            }

            return JsonResponse("{}");
        })));

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("backup"));
        var result = await service.UploadAsync("backups/database/2026/07/payplanner.backup", stream, "application/octet-stream", true, CancellationToken.None);

        Assert.Equal("/backups/database/2026/07/payplanner.backup", result.Path);
        Assert.Contains(requests, item => item.Contains("/resources?path=disk%3A%2FPayPlanner%2Fbackups", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/resources?path=disk%3A%2FPayPlanner%2Fbackups%2Fdatabase%2F2026%2F07", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/resources/upload", StringComparison.Ordinal) && item.Contains("payplanner.backup", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.StartsWith("PUT https://upload.local/backup", StringComparison.Ordinal));
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
            "Счет",
            "INV-1",
            new DateTime(2026, 6, 1),
            "Счет",
            "Клиент",
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

    private static string ReadFirstConnectionToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement[0].GetProperty("oAuthToken").GetString()!;
    }

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
