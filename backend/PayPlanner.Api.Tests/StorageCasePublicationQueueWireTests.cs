using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCasePublicationQueueWireTests
{
    [Fact]
    public async Task OnlyAdminReadsQueueWithNoStoreAndWithoutSensitiveOperationFields()
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync();
        const string email = "synthetic-queue-reader@payplanner.local";
        const string password = "SyntheticQueueReader1!";
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal("false", await db.SystemSettings.Where(item => item.Key == "notifications.email.enabled")
                .Select(item => item.Value).SingleAsync());
            var employee = new User
            {
                Email = email,
                FullName = "Synthetic queue reader",
                IsActive = true,
                IsApproved = true,
                Role = await db.Roles.SingleOrDefaultAsync(item => item.Name == "Юрист") ?? new Role { Name = "Юрист" }
            };
            employee.PasswordHash = new PasswordHasher<User>().HashPassword(employee, password);
            db.Users.Add(employee);
            var storageObject = new StorageObject
            {
                DataClass = "case-document",
                OwnerType = "case-document",
                OwnerId = "synthetic-case",
                LogicalName = "synthetic-wire.pdf",
                ContentType = "application/pdf"
            };
            db.StorageOperations.Add(new StorageOperation
            {
                StorageObject = storageObject,
                OperationType = StorageOperationTypes.PublishCaseDocument,
                Status = StorageOperationStatuses.DeadLetter,
                IdempotencyKey = "synthetic-wire-case-publication",
                PayloadJson = "sensitive-payload",
                LastError = "sensitive-provider-error",
                LeaseOwner = "sensitive-worker"
            });
            await db.SaveChangesAsync();
        }
        const string path = "/api/storage/operations/case-documents?status=deadLetter&limit=1";
        using (var anonymous = await runtime.Client.GetAsync(path)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email, password }))
        {
            login.EnsureSuccessStatusCode();
            var result = await login.Content.ReadFromJsonAsync<LoginResponse>();
            runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Assert.IsType<LoginResponse>(result).Token);
        }
        using (var forbidden = await runtime.Client.GetAsync(path)) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = runtime.Email, password = runtime.Password }))
        {
            login.EnsureSuccessStatusCode();
            var result = await login.Content.ReadFromJsonAsync<LoginResponse>();
            runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Assert.IsType<LoginResponse>(result).Token);
        }
        using var response = await runtime.Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive-payload", json);
        Assert.DoesNotContain("sensitive-provider-error", json);
        Assert.DoesNotContain("sensitive-worker", json);
        using var document = JsonDocument.Parse(json);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("synthetic-wire.pdf", item.GetProperty("fileName").GetString());
        Assert.False(item.GetProperty("canRetry").GetBoolean());
        Assert.EndsWith("Z", item.GetProperty("createdAt").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        using var invalid = await runtime.Client.GetAsync("/api/storage/operations/case-documents?limit=201");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        runtime.AssertNoServerErrors();
    }
}
