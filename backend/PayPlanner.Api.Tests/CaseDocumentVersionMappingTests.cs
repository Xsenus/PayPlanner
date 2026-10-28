using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseDocumentVersionMappingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyKeyDocumentsStayVisibleInCaseRegistryAndDetail(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_case_versions_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString);
        if (!postgres) await keeper.OpenAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString).Options);
        if (!postgres) await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "case-own", Permissions = [new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.Own }] };
        var user = new User { Email = "case-owner@example.test", FullName = "Автор", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var other = new User { Email = "case-other@example.test", FullName = "Другой", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        db.Users.AddRange(user, other); await db.SaveChangesAsync();
        var visibleClient = new Client { Name = "Видимый клиент", IsActive = true, ResponsibleUserId = user.Id };
        var hiddenClient = new Client { Name = "Скрытый клиент", IsActive = true, ResponsibleUserId = other.Id };
        var owned = new ClientCase { Client = visibleClient, Title = "Доступное дело", ResponsibleUserId = user.Id };
        var hidden = new ClientCase { Client = hiddenClient, Title = "Закрытое дело", ResponsibleUserId = other.Id };
        db.ClientCases.AddRange(owned, hidden); await db.SaveChangesAsync();
        ClientCaseDocument Doc(ClientCase @case, string key, int version, string name) => new()
        { ClientCaseId = @case.Id, DocumentKey = key, Version = version, OriginalFileName = name, StoredFileName = name, StoragePath = "synthetic", DocumentType = "Документ", Status = "Готово" };
        var first = Doc(owned, "", 1, "Без ключа 1.pdf");
        var second = Doc(owned, "", 2, "Без ключа 2.pdf");
        var old = Doc(owned, "claim", 1, "Иск 1.pdf");
        var current = Doc(owned, "claim", 2, "Иск 2.pdf");
        var secret = Doc(hidden, "", 9, "Чужой.pdf");
        db.ClientCaseDocuments.AddRange(first, second, old, current, secret); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var controller = new CasesV2Controller(db, caseDocumentStorage: new ExistingStorage())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.FullName)], "test")) } } };
        var caseResult = await controller.GetDocuments(owned.Id, CancellationToken.None);
        var listed = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(Assert.IsType<OkObjectResult>(caseResult.Result).Value);
        Assert.True(Assert.Single(listed, item => item.Id == first.Id).IsLatestVersion);
        Assert.True(Assert.Single(listed, item => item.Id == second.Id).IsLatestVersion);
        Assert.False(Assert.Single(listed, item => item.Id == old.Id).IsLatestVersion);
        var registryResult = Assert.IsType<OkObjectResult>(await controller.GetDocumentRegistry(owned.Id, null, null, null, null));
        var registry = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(registryResult.Value!.GetType().GetProperty("items")!.GetValue(registryResult.Value));
        Assert.Equal(new[] { first.Id, second.Id, current.Id }.Order(), registry.Select(item => item.Id).Order());
        Assert.All(registry, item => Assert.True(item.IsLatestVersion));
        var detail = await controller.GetDocumentById(first.Id, CancellationToken.None);
        Assert.True(Assert.IsType<ClientCaseDocumentDto>(Assert.IsType<OkObjectResult>(detail.Result).Value).IsLatestVersion);
        var hiddenResult = await controller.GetDocumentById(secret.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(hiddenResult.Result);
    }

    private sealed class ExistingStorage : ICaseDocumentStorage
    {
        public bool Exists(string storagePath) => true;
        public string BuildRelativePath(int caseId, string storedFileName) => throw new NotSupportedException();
        public string ResolvePath(string storagePath) => throw new NotSupportedException();
        public Task SaveAsync(string storagePath, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(string storagePath, byte[] content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void DeleteIfExists(string storagePath) => throw new NotSupportedException();
    }

    [Fact]
    public void EmptyKeysRemainIndependentWhileNamedVersionsStayGrouped()
    {
        var documents = new[]
        {
            new ClientCaseDocument { Id = 11, ClientCaseId = 3, DocumentKey = "", Version = 1, OriginalFileName = "Без ключа 1.pdf" },
            new ClientCaseDocument { Id = 12, ClientCaseId = 3, DocumentKey = "", Version = 2, OriginalFileName = "Без ключа 2.pdf" },
            new ClientCaseDocument { Id = 13, ClientCaseId = 3, DocumentKey = "Claim", Version = 1, OriginalFileName = "Иск 1.pdf" },
            new ClientCaseDocument { Id = 14, ClientCaseId = 3, DocumentKey = "claim", Version = 2, OriginalFileName = "Иск 2.pdf" },
        };
        var mapper = typeof(CasesV2Controller).GetMethod("MapDocuments", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(mapper.Invoke(null, new object?[] { documents, null }));
        var first = Assert.Single(result, item => item.Id == 11);
        var second = Assert.Single(result, item => item.Id == 12);
        Assert.True(first.IsLatestVersion);
        Assert.True(second.IsLatestVersion);
        Assert.Null(first.VersionGroupId);
        Assert.Null(second.VersionGroupId);
        Assert.False(Assert.Single(result, item => item.Id == 13).IsLatestVersion);
        Assert.True(Assert.Single(result, item => item.Id == 14).IsLatestVersion);
        Assert.Equal(13, Assert.Single(result, item => item.Id == 14).VersionGroupId);
    }
}
