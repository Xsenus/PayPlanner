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

public sealed class StorageStageRecoveryWireTests
{
    [Fact]
    public async Task BrowserConfirmsOnlyCompleteStageAgainstOwnedBackend()
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync();
        var root = Path.Combine(runtime.Root, "objects");
        var stageRoot = Path.Combine(root, ".task-uploads");
        Directory.CreateDirectory(stageRoot);
        var completePath = Path.Combine(stageRoot, "complete.tmp");
        var partialPath = Path.Combine(stageRoot, "partial.tmp");
        await File.WriteAllBytesAsync(completePath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(partialPath, [1, 2]);
        var resourceKey = JsonSerializer.Serialize(new string?[] { "local", null });
        var group = LocalStorageCapacityProbe.Observe(root, resourceKey, 0).GroupId;
        Assert.False(string.IsNullOrWhiteSpace(group));
        var old = DateTime.UtcNow.AddHours(-1);
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled")
                .Select(s => s.Value).SingleAsync());
            db.StorageCapacityReservations.AddRange(
                NewReservation('a', ".task-uploads/complete.tmp"),
                NewReservation('b', ".task-uploads/partial.tmp"),
                NewReservation('d', "../outside.tmp"));
            await db.SaveChangesAsync();
        }
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login",
            new { email = runtime.Email, password = runtime.Password });
        login.EnsureSuccessStatusCode();
        var admin = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(admin);
        await runtime.CheckStorageCapacityBrowserAsync(admin.Token);
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(StorageCapacityReservationStatuses.Released,
                await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == "stage:" + new string('a', 64))
                    .Select(r => r.Status).SingleAsync());
            Assert.Equal(2, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(completePath));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partialPath));
        runtime.AssertNoServerErrors();

        StorageCapacityReservation NewReservation(char suffix, string relativePath) => new()
        {
            IdempotencyKey = "stage:" + new string(suffix, 64),
            ResourceKey = resourceKey,
            GroupId = group,
            BindingHash = new string('c', 64),
            Bytes = 4,
            ObjectId = Guid.NewGuid(),
            LocalLocator = relativePath,
            CreatedAt = old,
            UpdatedAt = old,
        };
    }

    [Fact]
    public async Task AdminCanPreviewAndConfirmOnlyCompleteOwnedStageOverHttp()
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync();
        var root = Path.Combine(runtime.Root, "objects");
        var stageRoot = Path.Combine(root, ".task-uploads");
        Directory.CreateDirectory(stageRoot);
        var completePath = Path.Combine(stageRoot, "complete.tmp");
        var partialPath = Path.Combine(stageRoot, "partial.tmp");
        await File.WriteAllBytesAsync(completePath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(partialPath, [1, 2]);
        var resourceKey = JsonSerializer.Serialize(new string?[] { "local", null });
        var group = LocalStorageCapacityProbe.Observe(root, resourceKey, 0).GroupId;
        Assert.False(string.IsNullOrWhiteSpace(group));
        var old = DateTime.UtcNow.AddHours(-1);
        var completeKey = "stage:" + new string('a', 64);
        var partialKey = "stage:" + new string('b', 64);
        var unsafeKey = "stage:" + new string('d', 64);
        const string restrictedEmail = "synthetic-employee@payplanner.local";
        const string restrictedPassword = "SyntheticEmployee1!";
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled")
                .Select(s => s.Value).SingleAsync());
            var role = await db.Roles.SingleOrDefaultAsync(r => r.Name == "Юрист") ?? new Role { Name = "Юрист" };
            var employee = new User
            {
                Email = restrictedEmail,
                FullName = "Synthetic employee",
                Role = role,
                IsActive = true,
                IsApproved = true
            };
            employee.PasswordHash = new PasswordHasher<User>().HashPassword(employee, restrictedPassword);
            db.Users.Add(employee);
            db.StorageCapacityReservations.AddRange(
                NewReservation(completeKey, ".task-uploads/complete.tmp"),
                NewReservation(partialKey, ".task-uploads/partial.tmp"),
                NewReservation(unsafeKey, "../outside.tmp"));
            await db.SaveChangesAsync();
        }

        using (var anonymous = await runtime.Client.GetAsync("/api/storage/health"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var restrictedLogin = await runtime.Client.PostAsJsonAsync("/api/auth/login",
            new { email = restrictedEmail, password = restrictedPassword });
        restrictedLogin.EnsureSuccessStatusCode();
        var restricted = await restrictedLogin.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(restricted);
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", restricted.Token);
        using (var forbidden = await runtime.Client.PostAsync("/api/storage/capacity/stages/reconcile?apply=true", null))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var adminLogin = await runtime.Client.PostAsJsonAsync("/api/auth/login",
            new { email = runtime.Email, password = runtime.Password });
        adminLogin.EnsureSuccessStatusCode();
        var admin = await adminLogin.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(admin);
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);
        using (var healthResponse = await runtime.Client.GetAsync("/api/storage/health"))
        {
            healthResponse.EnsureSuccessStatusCode();
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            Assert.Equal(3, health.RootElement.GetProperty("pendingCapacityReservations").GetInt32());
            Assert.Equal(3, health.RootElement.GetProperty("pendingStageCapacityReservations").GetInt32());
        }
        using (var previewResponse = await runtime.Client.PostAsync("/api/storage/capacity/stages/reconcile", null))
        {
            previewResponse.EnsureSuccessStatusCode();
            using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, preview.RootElement.GetProperty("eligible").GetInt32());
            Assert.Equal(0, preview.RootElement.GetProperty("settled").GetInt32());
            var items = preview.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var complete = Assert.Single(items, item => item.GetProperty("key").GetString() == completeKey);
            Assert.Equal("eligible", complete.GetProperty("status").GetString());
            Assert.Equal(".task-uploads/complete.tmp", complete.GetProperty("relativePath").GetString());
            Assert.Equal(4, complete.GetProperty("expectedBytes").GetInt64());
            Assert.Equal(4, complete.GetProperty("observedBytes").GetInt64());
            Assert.DoesNotContain(root, complete.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var partial = Assert.Single(items, item => item.GetProperty("key").GetString() == partialKey);
            Assert.Equal("incompleteFile", partial.GetProperty("status").GetString());
            Assert.Equal(2, partial.GetProperty("observedBytes").GetInt64());
            var unsafeItem = Assert.Single(items, item => item.GetProperty("key").GetString() == unsafeKey);
            Assert.Equal("invalidAuthority", unsafeItem.GetProperty("status").GetString());
            Assert.False(unsafeItem.TryGetProperty("relativePath", out _));
            Assert.False(unsafeItem.TryGetProperty("observedBytes", out _));
            Assert.DoesNotContain("../outside.tmp", unsafeItem.GetRawText(), StringComparison.Ordinal);
        }
        await using (var db = runtime.OpenDatabase())
            Assert.Equal(3, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        using (var applyResponse = await runtime.Client.PostAsync("/api/storage/capacity/stages/reconcile?apply=true", null))
        {
            applyResponse.EnsureSuccessStatusCode();
            using var applied = JsonDocument.Parse(await applyResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, applied.RootElement.GetProperty("settled").GetInt32());
        }
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(StorageCapacityReservationStatuses.Released,
                await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == completeKey).Select(r => r.Status).SingleAsync());
            Assert.Equal(StorageCapacityReservationStatuses.Pending,
                await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == partialKey).Select(r => r.Status).SingleAsync());
            Assert.Equal(StorageCapacityReservationStatuses.Pending,
                await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == unsafeKey).Select(r => r.Status).SingleAsync());
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(completePath));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partialPath));
        runtime.AssertNoServerErrors();

        StorageCapacityReservation NewReservation(string key, string relativePath) => new()
        {
            IdempotencyKey = key,
            ResourceKey = resourceKey,
            GroupId = group,
            BindingHash = new string('c', 64),
            Bytes = 4,
            ObjectId = Guid.NewGuid(),
            LocalLocator = relativePath,
            CreatedAt = old,
            UpdatedAt = old,
        };
    }
}
