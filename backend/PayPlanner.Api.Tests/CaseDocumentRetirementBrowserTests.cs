using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseDocumentRetirementBrowserTests : IClassFixture<SqliteMigratedTemplate>, IClassFixture<CaseRetirementBrowserBundle>
{
    private readonly SqliteMigratedTemplate sqliteTemplate;
    private readonly CaseRetirementBrowserBundle? browserBundle;

    public CaseDocumentRetirementBrowserTests(SqliteMigratedTemplate sqliteTemplate, CaseRetirementBrowserBundle? browserBundle)
    {
        this.sqliteTemplate = sqliteTemplate;
        this.browserBundle = browserBundle;
    }

    [Theory]
    [InlineData(false, "documents")]
    [InlineData(true, "documents")]
    [InlineData(false, "edit")]
    [InlineData(false, "published-edit")]
    [InlineData(false, "modal")]
    [InlineData(false, "published-modal")]
    [InlineData(false, "writ")]
    [InlineData(false, "modal-writ")]
    [InlineData(false, "version")]
    [InlineData(false, "modal-version")]
    [InlineData(false, "revoked-edit")]
    [InlineData(false, "revoked-modal")]
    [InlineData(true, "restart-edit")]
    [InlineData(true, "restart-modal")]
    [InlineData(true, "restart-slow-edit")]
    [InlineData(true, "restart-slow-modal")]
    public async Task RealBrowserRetiresOwnedAttemptAndSeparatelyUploadsDocumentAndVersion(bool lostAcknowledgement, string flow)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(uploadCapacityChecks: true, template: sqliteTemplate,
            retirementBundle: browserBundle);
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = runtime.Email, password = runtime.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authentication = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        var key = Guid.NewGuid();
        var writAttempt = flow.Contains("writ", StringComparison.Ordinal);
        var versionAttempt = flow.Contains("version", StringComparison.Ordinal);
        var revoked = flow.StartsWith("revoked-", StringComparison.Ordinal);
        var restarted = flow.StartsWith("restart-", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(writAttempt ? "%PDF-1.4\nOwned browser writ\n%%EOF" : "Owned browser retirement bytes");
        var pendingFileName = writAttempt ? "retirement-browser-owned.pdf" : "retirement-browser-owned.bin";
        var retainedPath = Path.Combine(runtime.Root, "retirement-browser-owned.bin");
        await File.WriteAllBytesAsync(retainedPath, bytes);
        int caseId;
        int clientId;
        var sourceDocumentId = 0;
        string? requestedDocumentKey = null;
        var publishedWinner = flow.StartsWith("published-", StringComparison.Ordinal);
        await using (var db = runtime.OpenDatabase())
        {
            var client = new Client { Name = "Synthetic retirement browser client", ResponsibleUserId = authentication.User.Id };
            var clientCase = new ClientCase
            {
                Client = client,
                Title = "Synthetic retirement browser case",
                ResponsibleUserId = authentication.User.Id,
                Strategy = "Persisted synthetic strategy"
            };
            db.ClientCases.Add(clientCase);
            await db.SaveChangesAsync();
            caseId = clientCase.Id;
            clientId = client.Id;
            if (versionAttempt)
            {
                using var body = new MultipartFormDataContent();
                using var source = new ByteArrayContent(Encoding.UTF8.GetBytes("Base browser version bytes"));
                source.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                body.Add(source, "file", "base-browser-version.bin");
                body.Add(new StringContent("Документ"), "documentType");
                body.Add(new StringContent("Подготовлен"), "status");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/cases/{caseId}/documents") { Content = body };
                request.Headers.Add("X-Upload-Key", Guid.NewGuid().ToString("D"));
                using var response = await runtime.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var document = (await response.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!;
                Assert.Equal(1, document.Version);
                sourceDocumentId = document.Id;
                requestedDocumentKey = document.DocumentKey;
            }
            var resource = JsonSerializer.Serialize(new string?[] { "local", null });
            var capacity = LocalStorageCapacityProbe.Observe(runtime.Root, resource, 0);
            Assert.NotNull(capacity.AvailableBytes);
            Assert.NotEmpty(capacity.GroupId);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var intent = new
            {
                CaseId = caseId,
                ActorId = authentication.User.Id,
                ActorName = authentication.User.FullName,
                UploadKey = key,
                RequestedDocumentKey = requestedDocumentKey,
                DocumentKey = requestedDocumentKey ?? Guid.NewGuid().ToString("N"),
                Version = versionAttempt ? 2 : 1,
                FileName = pendingFileName,
                ContentType = writAttempt ? "application/pdf" : "application/octet-stream",
                DocumentType = writAttempt ? "Исполнительный лист" : "Документ",
                Status = writAttempt ? "Получен" : "Подготовлен",
                SizeBytes = (long)bytes.Length,
                Sha256 = sha,
                UploadedAt = DateTime.UtcNow,
                DocumentId = (int?)null
            };
            var intentJson = JsonSerializer.Serialize(intent, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (publishedWinner)
            {
                var environment = new BrowserEnvironment(runtime.Root);
                var monitor = new SyntheticFixedOptions<StorageOrchestrationOptions>(runtime.Configuration
                    .GetSection(StorageOrchestrationOptions.SectionName).Get<StorageOrchestrationOptions>()!);
                var registry = new StorageProviderRegistry([new LocalStorageProvider(environment, runtime.Configuration)]);
                var storage = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
                await using var input = new MemoryStream(bytes);
                var stored = await storage.WriteAsync(new("case-document", "case-document", $"upload:case:{caseId}:{authentication.User.Id}:{key:N}",
                    intent.FileName, intent.ContentType, input, $"case-document:{caseId}:{authentication.User.Id}:{key:N}",
                    authentication.User.Id, intentJson, bytes.Length, sha), CancellationToken.None);
                Assert.Equal("available", stored.Status);
                Assert.NotEqual(Guid.Empty, stored.ObjectId);
                Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
            }
            else
            {
                db.StorageCapacityReservations.Add(new StorageCapacityReservation
                {
                    IdempotencyKey = StorageObjectService.GetCapacityReservationKey($"case-document:{caseId}:{authentication.User.Id}:{key:N}"),
                    ResourceKey = resource,
                    GroupId = capacity.GroupId,
                    BindingHash = sha,
                    Bytes = bytes.Length,
                    ObjectId = Guid.NewGuid(),
                    LocalLocator = retainedPath,
                    ProductContextJson = intentJson
                });
                await db.SaveChangesAsync();
            }
        }
        await runtime.CheckCaseRetirementBrowserAsync(caseId, key, lostAcknowledgement, flow, clientId, sourceDocumentId, requestedDocumentKey,
            revoked ? async () =>
            {
                await using var changes = runtime.OpenDatabase();
                var role = new Role
                {
                    Name = "Synthetic retirement observer",
                    Permissions = [new RolePermission
                    { Section = RolePermissionCatalog.Cases, CanView = true, CanEdit = false, ViewScope = DataVisibilityScope.All }]
                };
                changes.Roles.Add(role);
                var actor = await changes.Users.SingleAsync(u => u.Id == authentication.User.Id);
                actor.Role = role;
                await changes.SaveChangesAsync();
            }
        : restarted ? async () =>
            {
                await using (var committed = runtime.OpenDatabase())
                {
                    var marker = Assert.Single(await committed.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
                    Assert.Equal(caseId, marker.CaseId);
                    Assert.Equal(key, marker.UploadKey);
                    Assert.Equal(authentication.User.Id, marker.ActorId);
                    Assert.Empty(await committed.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
                }
                await runtime.RestartAsync(flow.Contains("slow", StringComparison.Ordinal) ? TimeSpan.FromSeconds(12) : TimeSpan.Zero);
            }
        : null);
        await using var verification = runtime.OpenDatabase();
        if (revoked)
        {
            Assert.Empty(await verification.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verification.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verification.StorageProductBindings.AsNoTracking().ToArrayAsync());
            var actor = await verification.Users.Include(u => u.Role).ThenInclude(r => r!.Permissions)
                .SingleAsync(u => u.Id == authentication.User.Id);
            Assert.Equal("Synthetic retirement observer", actor.Role!.Name);
            Assert.False(Assert.Single(actor.Role.Permissions).CanEdit);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(retainedPath));
            Assert.Equal("Persisted synthetic strategy", (await verification.ClientCases.SingleAsync(c => c.Id == caseId)).Strategy);
            Assert.Equal("false", await verification.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
            runtime.AssertNoServerErrors();
            return;
        }
        if (publishedWinner) Assert.Empty(await verification.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        else
        {
            var marker = Assert.Single(await verification.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
            Assert.Equal(caseId, marker.CaseId);
            Assert.Equal(key, marker.UploadKey);
            Assert.Equal(authentication.User.Id, marker.ActorId);
        }
        var documents = await verification.ClientCaseDocuments.AsNoTracking().OrderBy(d => d.Version).ToArrayAsync();
        Assert.Equal(flow == "documents" || publishedWinner || versionAttempt ? 2 : 1, documents.Length);
        if (flow == "documents" || versionAttempt)
        {
            Assert.Equal(new[] { 1, 2 }, documents.Select(d => d.Version));
            Assert.Equal(documents[0].DocumentKey, documents[1].DocumentKey);
        }
        else
        {
            Assert.All(documents, document => Assert.Equal(1, document.Version));
            Assert.Single(documents, d => d.DocumentType == "Исполнительный лист");
        }
        Assert.Equal(documents.Length, await verification.StorageProductBindings.CountAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(retainedPath));
        Assert.Equal("Persisted synthetic strategy", (await verification.ClientCases.SingleAsync(c => c.Id == caseId)).Strategy);
        foreach (var document in documents)
        {
            using var download = await runtime.Client.GetAsync($"/api/case-documents/{document.Id}/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var expected = versionAttempt ? document.Version == 1 ? "Base browser version bytes" : "Owned browser retirement bytes"
                : flow == "documents" ? document.Version == 1 ? "Separate browser document" : "Separate browser version"
                : document.DocumentType == "Исполнительный лист" ? "%PDF-1.4\nOwned browser writ\n%%EOF" : "Owned browser retirement bytes";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), await download.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal("false", await verification.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        runtime.AssertNoServerErrors();
    }

    private sealed class BrowserEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api";
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class CaseDocumentRetirementSharedBrowserTests : IClassFixture<SqliteMigratedTemplate>
{
    private readonly SqliteMigratedTemplate sqliteTemplate;

    public CaseDocumentRetirementSharedBrowserTests(SqliteMigratedTemplate sqliteTemplate) => this.sqliteTemplate = sqliteTemplate;

    [Fact]
    public Task RealBrowserRetirementUsesSharedProductionArtifact()
        => new CaseDocumentRetirementBrowserTests(sqliteTemplate, null)
            .RealBrowserRetiresOwnedAttemptAndSeparatelyUploadsDocumentAndVersion(false, "documents");
}
