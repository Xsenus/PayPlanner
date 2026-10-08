using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, "live")]
    [InlineData(true, "live")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "partial")]
    [InlineData(true, "partial")]
    [InlineData(false, "complete")]
    [InlineData(true, "complete")]
    [InlineData(false, "missing-marker")]
    [InlineData(true, "missing-marker")]
    [InlineData(false, "changed-proof")]
    [InlineData(true, "changed-proof")]
    public async Task IndependentWriterFenceSurvivesAgeAndAllowsOnlyProvenRecovery(bool postgres, string scenario)
    {
        await using var fixture = await WriterProcessFixture.CreateAsync(postgres);
        var assembly = Path.Combine(AppContext.BaseDirectory, "PayPlanner.StorageStageWriterProbe.dll");
        Assert.True(File.Exists(assembly), "The process probe must be built with the test project.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = fixture.Root
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "WINDIR", "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["Email__Enabled"] = "false";
        start.Environment["BackgroundServices__Enabled"] = "false";
        if (postgres) start.Environment["PAYPLANNER_TEST_POSTGRES"] = fixture.AdminConnectionString!;
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(postgres ? "postgres" : "sqlite");
        start.ArgumentList.Add(fixture.Root);
        start.ArgumentList.Add(scenario is "partial" or "complete" ? scenario : "missing");
        using var writer = Process.Start(start) ?? throw new InvalidOperationException("Owned writer did not start.");
        var processIdentity = OwnedProcessIdentity.Capture(writer);
        var output = writer.StandardOutput.ReadToEndAsync();
        var errors = writer.StandardError.ReadToEndAsync();
        try
        {
            var readyPath = Path.Combine(fixture.Root, "writer-ready.json");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(readyPath) && !writer.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(File.Exists(readyPath), writer.HasExited ? await errors : "Writer readiness timed out.");
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath));
            Assert.Equal(writer.Id, ready.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.Equal(processIdentity, ready.RootElement.GetProperty("ProcessIdentity").GetString());
            var ticket = ready.RootElement.GetProperty("Ticket").Deserialize<StorageStageCapacityTicket>()!;
            Assert.StartsWith("stage:writer-v1:", ticket.Key);
            var marker = Path.Combine(fixture.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
            Assert.True(File.Exists(marker));
            var old = DateTime.UtcNow.AddHours(-12);
            await fixture.Db.StorageCapacityReservations.Where(row => row.IdempotencyKey == ticket.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedAt, old).SetProperty(row => row.UpdatedAt, old));
            // The independent child has received a durable ticket. Twelve hours
            // of apparent age must not allow recovery to bypass its live fence.
            foreach (var apply in new[] { false, true })
            {
                var live = await fixture.Stages.ReconcilePendingAsync(apply, 10, TimeSpan.FromMinutes(10), default);
                Assert.Equal("writerNotProvenStopped", Assert.Single(live.Items).Status);
                Assert.Equal(0, live.Settled);
                Assert.Equal(StorageCapacityReservationStatuses.Pending, await fixture.StatusAsync(ticket.Key));
            }
            if (scenario == "live") return;
            await StopOwnedWriterAsync();
            if (scenario is "missing-marker" or "changed-proof")
            {
                // Process exit and kernel handle rundown are separate events.
                // Prepare tampering only after the SAME marker can be opened
                // exclusively; do not weaken either live-writer assertion.
                var quiescentDeadline = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    try
                    {
                        await using var exclusive = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        break;
                    }
                    catch (IOException) when (DateTime.UtcNow < quiescentDeadline) { await Task.Delay(20); }
                }
            }
            if (scenario == "missing-marker") File.Delete(marker);
            if (scenario == "changed-proof") await File.WriteAllTextAsync(marker, new string('0', 64));
            var recoverable = scenario is "missing" or "complete";
            var preview = await fixture.Stages.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), default);
            var expected = scenario switch
            {
                "missing" => "eligibleMissingStage",
                "complete" => "eligible",
                "partial" => "incompleteFile",
                _ => "writerNotProvenStopped"
            };
            Assert.Equal(expected, Assert.Single(preview.Items).Status);
            Assert.Equal(recoverable ? 1 : 0, preview.Eligible);
            Assert.Equal(0, preview.Settled);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await fixture.StatusAsync(ticket.Key));
            var applied = await fixture.Stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(recoverable ? 1 : 0, applied.Settled);
            Assert.Equal(recoverable ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending,
                await fixture.StatusAsync(ticket.Key));
            if (scenario == "missing")
            {
                Assert.Equal("releasedMissingStage", Assert.Single(applied.Items).Status);
                Assert.False(File.Exists(ticket.AbsolutePath));
                Assert.False(File.Exists(marker));
            }
            if (scenario == "missing-marker") Assert.False(File.Exists(marker));
            if (scenario is "partial" or "complete")
                Assert.Equal(scenario == "complete" ? new byte[] { 1, 2, 3, 4 } : new byte[] { 1, 2 },
                    await File.ReadAllBytesAsync(ticket.AbsolutePath));
            Assert.Equal(1, await fixture.Db.StorageCapacityReservations.CountAsync());
            Assert.Equal(0, await fixture.Db.StorageObjects.CountAsync());
        }
        finally
        {
            await StopOwnedWriterAsync();
            await output;
            await errors;
        }

        async Task StopOwnedWriterAsync()
        {
            if (writer.HasExited) return;
            using var observed = Process.GetProcessById(writer.Id);
            Assert.Equal(processIdentity, OwnedProcessIdentity.Capture(observed));
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class WriterProcessFixture : IAsyncDisposable
    {
        private const string Prefix = "pp_stage_fence_";
        private readonly string _name;
        private readonly NpgsqlConnection? _admin;
        private readonly string? _probeConnectionString;
        public string Root { get; }
        public PaymentContext Db { get; }
        public StorageStageCapacityService Stages { get; }
        public string? AdminConnectionString => _probeConnectionString;

        private WriterProcessFixture(string name, string root, PaymentContext db, NpgsqlConnection? admin, string? probeConnectionString)
        {
            (_name, Root, Db, _admin) = (name, root, db, admin);
            _probeConnectionString = probeConnectionString;
            Stages = CreateStages(db);
        }

        public StorageStageCapacityService CreateStages(PaymentContext context)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LocalStoragePaths.RootConfigurationKey] = Path.Combine(Root, "objects"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var environment = new TestEnvironment(Root);
            return new(context, new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]),
                new StaticOptions(new() { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 }),
                environment, configuration);
        }

        public PaymentContext NewContext(params IInterceptor[] interceptors) => Db.Database.IsNpgsql()
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(Db.Database.GetConnectionString()).AddInterceptors(interceptors)
                .Options, useObjectAuditModel: false)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(Db.Database.GetConnectionString()).AddInterceptors(interceptors).Options);

        public static async Task<WriterProcessFixture> CreateAsync(bool postgres, params IInterceptor[] interceptors)
        {
            var name = Prefix + Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(root);
            PaymentContext? db = null;
            NpgsqlConnection? admin = null;
            string? probeConnectionString = null;
            try
            {
                if (postgres)
                {
                    Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")));
                    var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES"))
                    { Database = "postgres", Pooling = false };
                    Assert.True(settings.Host is "127.0.0.1" or "::1");
                    admin = new(settings.ConnectionString);
                    await admin.OpenAsync();
                    await using var create = admin.CreateCommand();
                    create.CommandText = $"CREATE DATABASE \"{name}\"";
                    await create.ExecuteNonQueryAsync();
                    Console.WriteLine($"Owned stage fence database: {name}");
                    settings.Database = name;
                    probeConnectionString = settings.ConnectionString;
                    // Migrations use the complete snapshot model; runtime fault injection stays scoped.
                    await using (var migrationDb = new PostgreSqlPaymentContext(
                        new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options))
                    {
                        await migrationDb.Database.MigrateAsync();
                        Assert.False(migrationDb.Database.HasPendingModelChanges());
                    }
                    db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                        .UseNpgsql(settings.ConnectionString).AddInterceptors(interceptors).Options,
                        useObjectAuditModel: false);
                }
                else
                {
                    db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                        .UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False").AddInterceptors(interceptors).Options);
                    await db.Database.EnsureCreatedAsync();
                    // Keep this fixture's unpooled connection alive while independent readers and the child writer run.
                    // Disposing Db closes only this owned connection before strict directory cleanup.
                    await db.Database.OpenConnectionAsync();
                }
                var emailSetting = await db.SystemSettings.SingleOrDefaultAsync(row => row.Key == "notifications.email.enabled");
                if (emailSetting is null) db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
                else emailSetting.Value = "false";
                await db.SaveChangesAsync();
                return new(name, root, db, admin, probeConnectionString);
            }
            catch
            {
                if (db is not null) await db.DisposeAsync();
                if (admin is not null) await admin.DisposeAsync();
                // Preserve a failed provision for diagnosis; do not force-drop it.
                throw;
            }
        }

        public Task<string> StatusAsync(string key) => Db.StorageCapacityReservations.AsNoTracking()
            .Where(row => row.IdempotencyKey == key).Select(row => row.Status).SingleAsync();

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (_admin is not null)
            {
                Assert.StartsWith(Prefix, _name);
                Assert.True(Guid.TryParseExact(_name[Prefix.Length..], "N", out _));
                await using var command = _admin.CreateCommand();
                command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = @name";
                command.Parameters.AddWithValue("name", _name);
                for (var attempt = 0; attempt < 50 && Convert.ToInt64(await command.ExecuteScalarAsync()) != 0; attempt++) await Task.Delay(100);
                Assert.Equal(0, Convert.ToInt64(await command.ExecuteScalarAsync()));
                command.Parameters.Clear();
                command.CommandText = $"DROP DATABASE \"{_name}\"";
                await command.ExecuteNonQueryAsync();
                await _admin.DisposeAsync();
            }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(Root)));
            Assert.Equal(_name, Path.GetFileName(Root));
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("PP_OWN_SQLITE_CLEANUP_FAILURE " + JsonSerializer.Serialize(new
                {
                    OwnedRoot = Path.GetFileName(Root),
                    ExceptionType = failure.GetType().FullName,
                    failure.HResult
                }));
                throw;
            }
        }
    }
}
