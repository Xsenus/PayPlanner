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

public sealed class StorageStageWriterRecoveryWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HttpRecoveryRequiresStoppedWriterAndFreshFileState(bool changeAfterPreview)
        => VerifyRecovery(changeAfterPreview, useBrowser: false);

    [Fact]
    public Task BrowserRecoveryRechecksChangedFileAndRetainsLiveWriter()
        => VerifyRecovery(changeAfterPreview: true, useBrowser: true);

    private static async Task VerifyRecovery(bool changeAfterPreview, bool useBrowser)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync();
        await using var db = runtime.OpenDatabase();
        Assert.Equal("false", await db.SystemSettings.Where(row => row.Key == "notifications.email.enabled")
            .Select(row => row.Value).SingleAsync());
        var root = Path.Combine(runtime.Root, "objects");
        var directory = Path.Combine(root, ".task-uploads");
        Directory.CreateDirectory(directory);
        var environment = new WriterWireEnvironment(runtime.Root);
        var options = new WriterWireOptions(new StorageOrchestrationOptions
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0,
        });
        var stages = new StorageStageCapacityService(db,
            new StorageProviderRegistry([new LocalStorageProvider(environment, runtime.Configuration)]),
            options, environment, runtime.Configuration);
        var tickets = new List<StorageStageCapacityTicket>();
        try
        {
            var missing = await Reserve("missing", null, closeWriter: true);
            var live = await Reserve("live", null, closeWriter: false);
            var partial = await Reserve("partial", [1, 2], closeWriter: true);
            var changed = await Reserve("changed", null, closeWriter: true);
            var old = DateTime.UtcNow.AddHours(-12);
            await db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, old));
            var epochBefore = await db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            const string employeeEmail = "stage-employee@payplanner.local";
            const string password = "SyntheticStageEmployee1!";
            var role = await db.Roles.SingleOrDefaultAsync(row => row.Name == "Юрист") ?? new Role { Name = "Юрист" };
            var employee = new User { Email = employeeEmail, FullName = "Synthetic stage employee", Role = role, IsActive = true, IsApproved = true };
            employee.PasswordHash = new PasswordHasher<User>().HashPassword(employee, password);
            db.Users.Add(employee);
            await db.SaveChangesAsync();
            using (var response = await runtime.Client.PostAsync("/api/storage/capacity/stages/reconcile?apply=true", null))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await Login(employeeEmail, password);
            foreach (var query in new[] { "", "?apply=true" })
            {
                using var response = await runtime.Client.PostAsync("/api/storage/capacity/stages/reconcile" + query, null);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            Assert.Equal(4, await db.StorageCapacityReservations.CountAsync(row => row.Status == StorageCapacityReservationStatuses.Pending));
            var adminToken = await Login(runtime.Email, runtime.Password);
            using (var preview = await Reconcile(apply: false))
            {
                Assert.Equal(4, preview.RootElement.GetProperty("inspected").GetInt32());
                Assert.Equal(2, preview.RootElement.GetProperty("eligible").GetInt32());
                Assert.Equal(0, preview.RootElement.GetProperty("settled").GetInt32());
                AssertStatus(preview, missing, "eligibleMissingStage");
                AssertStatus(preview, changed, "eligibleMissingStage");
                AssertStatus(preview, live, "writerNotProvenStopped");
                AssertStatus(preview, partial, "incompleteFile", observed: 2);
                Assert.DoesNotContain(root, preview.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            }
            if (useBrowser)
                await runtime.CheckStorageCapacityBrowserAsync(adminToken, changed.AbsolutePath);
            else
            {
                if (changeAfterPreview) await File.WriteAllBytesAsync(changed.AbsolutePath, [3, 4]);
                using var applied = await Reconcile(apply: true);
                Assert.Equal(changeAfterPreview ? 1 : 2, applied.RootElement.GetProperty("settled").GetInt32());
                AssertStatus(applied, missing, "releasedMissingStage");
                AssertStatus(applied, changed, changeAfterPreview ? "incompleteFile" : "releasedMissingStage",
                    observed: changeAfterPreview ? 2 : null);
                AssertStatus(applied, live, "writerNotProvenStopped");
                AssertStatus(applied, partial, "incompleteFile", observed: 2);
            }
            Assert.Equal(StorageCapacityReservationStatuses.Released, await Status(missing));
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await Status(live));
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await Status(partial));
            Assert.Equal(changeAfterPreview ? StorageCapacityReservationStatuses.Pending : StorageCapacityReservationStatuses.Released, await Status(changed));
            Assert.Equal(epochBefore + (changeAfterPreview ? 1 : 2), await db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.False(File.Exists(missing.AbsolutePath));
            Assert.False(File.Exists(live.AbsolutePath));
            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partial.AbsolutePath));
            if (changeAfterPreview) Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(changed.AbsolutePath));
            await live.CloseWriterAsync();
            using (var recovered = await Reconcile(apply: true))
            {
                Assert.Equal(1, recovered.RootElement.GetProperty("settled").GetInt32());
                AssertStatus(recovered, live, "releasedMissingStage");
            }
            var epochAfter = await db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            using (var repeated = await Reconcile(apply: true))
                Assert.Equal(0, repeated.RootElement.GetProperty("settled").GetInt32());
            Assert.Equal(epochAfter, await db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.Equal(changeAfterPreview ? 2 : 1, await db.StorageCapacityReservations.CountAsync(row => row.Status == StorageCapacityReservationStatuses.Pending));
            Assert.Equal(0, await db.StorageObjects.CountAsync());
            Assert.Equal(0, await db.StorageProductBindings.CountAsync());
            runtime.AssertNoServerErrors();

            async Task<StorageStageCapacityTicket> Reserve(string name, byte[]? bytes, bool closeWriter)
            {
                var path = Path.Combine(directory, name + ".tmp");
                var admission = await stages.ReserveAsync("wire-writer:" + name, path, 4, default);
                Assert.Null(admission.Error);
                var ticket = Assert.IsType<StorageStageCapacityTicket>(admission.Ticket);
                tickets.Add(ticket);
                if (bytes is not null) await File.WriteAllBytesAsync(path, bytes);
                if (closeWriter) await ticket.CloseWriterAsync();
                return ticket;
            }
            Task<string> Status(StorageStageCapacityTicket ticket) => db.StorageCapacityReservations.AsNoTracking()
                .Where(row => row.IdempotencyKey == ticket.Key).Select(row => row.Status).SingleAsync();
            async Task<string> Login(string email, string secret)
            {
                using var response = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email, password = secret });
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                Assert.NotNull(result);
                runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
                return result.Token;
            }
            async Task<JsonDocument> Reconcile(bool apply)
            {
                using var response = await runtime.Client.PostAsync("/api/storage/capacity/stages/reconcile" + (apply ? "?apply=true" : ""), null);
                response.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }
        }
        finally { foreach (var ticket in tickets) await ticket.CloseWriterAsync(); }
    }

    private static void AssertStatus(JsonDocument report, StorageStageCapacityTicket ticket, string status, long? observed = null)
    {
        var item = Assert.Single(report.RootElement.GetProperty("items").EnumerateArray().ToArray(),
            row => row.GetProperty("key").GetString() == ticket.Key);
        Assert.Equal(status, item.GetProperty("status").GetString());
        Assert.Equal(ticket.RelativePath, item.GetProperty("relativePath").GetString());
        Assert.Equal(4, item.GetProperty("expectedBytes").GetInt64());
        if (observed is not null) Assert.Equal(observed, item.GetProperty("observedBytes").GetInt64());
    }

    private sealed class WriterWireOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class WriterWireEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
