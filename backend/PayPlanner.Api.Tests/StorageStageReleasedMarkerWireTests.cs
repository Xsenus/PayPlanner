using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageStageReleasedMarkerWireTests
{
    private const string Endpoint = "/api/storage/capacity/stages/markers/reconcile";

    [Fact]
    public Task AdminCanRemoveOnlyProvenReleasedMarkerWithoutChangingRecordsOrForeignBytes() => Verify(false);

    [Fact]
    public Task BrowserRemovesOnlyProvenReleasedMarkerAndKeepsForeignBytes() => Verify(true);

    private static async Task Verify(bool useBrowser)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync();
        await using var db = runtime.OpenDatabase();
        Assert.Equal("false", await db.SystemSettings.Where(row => row.Key == "notifications.email.enabled").Select(row => row.Value).SingleAsync());
        var environment = new MarkerWireEnvironment(runtime.Root);
        Directory.CreateDirectory(Path.Combine(runtime.Root, "objects"));
        var stages = new StorageStageCapacityService(db,
            new StorageProviderRegistry([new LocalStorageProvider(environment, runtime.Configuration)]),
            new MarkerWireOptions(new() { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 }),
            environment, runtime.Configuration);
        var tickets = new List<StorageStageCapacityTicket>();
        try
        {
            var ended = await Reserve("ended", true, true);
            var live = await Reserve("live", true, false);
            var tampered = await Reserve("tampered", true, true);
            var pending = await Reserve("pending", false, false);
            await File.WriteAllBytesAsync(ended.AbsolutePath, [7, 8]);
            await File.WriteAllTextAsync(Marker(tampered), new string('f', 64));
            const string email = "marker-employee@payplanner.local";
            const string password = "SyntheticMarkerEmployee1!";
            var role = await db.Roles.SingleOrDefaultAsync(row => row.Name == "Юрист") ?? new Role { Name = "Юрист" };
            var employee = new User { Email = email, FullName = "Synthetic marker employee", Role = role, IsActive = true, IsApproved = true };
            employee.PasswordHash = new PasswordHasher<User>().HashPassword(employee, password);
            db.Users.Add(employee);
            await db.SaveChangesAsync();
            var before = await Records();
            var epoch = await db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            using (var response = await runtime.Client.PostAsync(Endpoint + "?apply=true", null))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await Login(email, password);
            foreach (var query in new[] { "", "?apply=true" })
            {
                using var response = await runtime.Client.PostAsync(Endpoint + query, null);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            var adminToken = await Login(runtime.Email, runtime.Password);
            foreach (var query in new[] { "?limit=0", "?limit=201", "?minimumAgeMinutes=9", "?minimumAgeMinutes=10081", "?afterKey=invalid" })
            {
                using var response = await runtime.Client.PostAsync(Endpoint + query, null);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            using (var preview = await Reconcile(false))
            {
                Assert.Equal(3, preview.RootElement.GetProperty("inspected").GetInt32());
                Assert.Equal(1, preview.RootElement.GetProperty("eligible").GetInt32());
                Assert.Equal(0, preview.RootElement.GetProperty("removed").GetInt32());
                Assert.DoesNotContain(runtime.Root, preview.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(pending.Key, preview.RootElement.GetRawText());
            }
            Assert.True(File.Exists(Marker(ended)));
            if (useBrowser) await runtime.CheckStorageCapacityBrowserAsync(adminToken, releasedMarkers: true);
            else using (var applied = await Reconcile(true)) Assert.Equal(1, applied.RootElement.GetProperty("removed").GetInt32());
            Assert.False(File.Exists(Marker(ended)));
            Assert.True(File.Exists(Marker(live)));
            Assert.True(File.Exists(Marker(pending)));
            Assert.Equal(new string('f', 64), await File.ReadAllTextAsync(Marker(tampered)));
            Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(ended.AbsolutePath));
            using (var repeat = await Reconcile(true)) Assert.Equal(0, repeat.RootElement.GetProperty("removed").GetInt32());
            await live.CloseWriterAsync();
            using (var stopped = await Reconcile(true)) Assert.Equal(1, stopped.RootElement.GetProperty("removed").GetInt32());
            Assert.Equal(before, await Records());
            Assert.Equal(epoch, await db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.Empty(await db.StorageObjects.ToArrayAsync());
            Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            runtime.AssertNoServerErrors();

            async Task<StorageStageCapacityTicket> Reserve(string name, bool released, bool close)
            {
                var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("marker-wire:" + name,
                    Path.Combine(runtime.Root, "objects", name + ".tmp"), 4, default)).Ticket);
                tickets.Add(ticket);
                if (close) await ticket.CloseWriterAsync();
                var old = DateTime.UtcNow.AddHours(-12);
                if (released) await db.StorageCapacityReservations.Where(row => row.IdempotencyKey == ticket.Key)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, StorageCapacityReservationStatuses.Released)
                        .SetProperty(row => row.ReleasedAt, old).SetProperty(row => row.UpdatedAt, old));
                return ticket;
            }
            string Marker(StorageStageCapacityTicket ticket) => Path.Combine(runtime.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
            async Task<string> Records() => JsonSerializer.Serialize(await db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey).ToArrayAsync());
            async Task<string> Login(string address, string secret)
            {
                using var response = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = address, password = secret });
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                Assert.NotNull(result);
                runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
                return result.Token;
            }
            async Task<JsonDocument> Reconcile(bool apply)
            {
                using var response = await runtime.Client.PostAsync(Endpoint + "?apply=" + apply.ToString().ToLowerInvariant(), null);
                response.EnsureSuccessStatusCode();
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }
        }
        finally { foreach (var ticket in tickets) await ticket.CloseWriterAsync(); }
    }

    private sealed class MarkerWireOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class MarkerWireEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
