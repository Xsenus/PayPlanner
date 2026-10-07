using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStagePhysicalSizeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFileReleaseRecoversOriginalUpperBoundReceiptWithoutAcceptingForgedAuthority(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        var original = await fixture.ReserveAsync();
        var confirmed = await fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None);
        File.Delete(original.AbsolutePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ReleaseAsync(original with { AbsolutePath = Path.Combine(fixture.Root, "foreign-missing.tmp") }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ReleaseAsync(original with { BindingHash = new string('b', 64) }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ReleaseAsync(original with { SizeBytes = 7 }, CancellationToken.None));
        Assert.Equal(StorageCapacityReservationStatuses.Pending, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        await fixture.Stage.ReleaseAsync(original, CancellationToken.None);
        var released = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, released.Status);
        Assert.Equal(confirmed.BindingHash, released.BindingHash);
        Assert.Equal(4, released.Bytes);
        Assert.Equal(3, await fixture.Db.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        await fixture.Stage.ReleaseAsync(original, CancellationToken.None);
        Assert.Equal(3, await fixture.Db.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedSizeKeepsIdentityAndRecoversDebtWithoutChangingBytesOrUnrelatedTrackedData(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        var original = await fixture.ReserveAsync();
        var client = new Client { Name = "persisted" };
        fixture.Db.Clients.Add(client);
        await fixture.Db.SaveChangesAsync();
        client.Name = "unsaved";
        var confirmed = await fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None);
        Assert.Equal(original.Key, confirmed.Key);
        Assert.Equal(original.ObjectId, confirmed.ObjectId);
        Assert.Equal(original.AbsolutePath, confirmed.AbsolutePath);
        Assert.Equal(original.RelativePath, confirmed.RelativePath);
        Assert.Equal(original.ResourceKey, confirmed.ResourceKey);
        Assert.Equal(original.GroupId, confirmed.GroupId);
        Assert.NotEqual(original.BindingHash, confirmed.BindingHash);
        Assert.Equal(4, confirmed.SizeBytes);
        Assert.Equal(EntityState.Modified, fixture.Db.Entry(client).State);
        await using var fresh = fixture.NewContext();
        Assert.Equal("persisted", await fresh.Clients.Where(item => item.Id == client.Id).Select(item => item.Name).SingleAsync());
        var replay = await fixture.CreateStage(fresh).ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None);
        Assert.Equal(confirmed, replay);
        Assert.Equal(2, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(4, row.Bytes);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        var old = DateTime.UtcNow.AddHours(-1);
        // The manual producer has finished; recovery must never bypass a live writer.
        await original.CloseWriterAsync();
        await fresh.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, old));
        var recovered = await fixture.CreateStage(fresh).ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.Equal(1, recovered.Settled);
        Assert.Equal(confirmed, await fixture.CreateStage(fresh).ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None));
        Assert.Equal(3, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(original.AbsolutePath));
        Assert.Equal(0, await fresh.StorageObjects.CountAsync());
        Assert.Equal(0, await fresh.StorageProductBindings.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommitFailurePreservesPhysicalBytesAndExactRepeatResolvesTheOriginalReservation(bool postgres, bool afterCommit)
    {
        var fault = new CommitFault(afterCommit);
        await using var fixture = await Fixture.CreateAsync(postgres, fault);
        var original = await fixture.ReserveAsync();
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None));
        await using var fresh = fixture.NewContext();
        var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(afterCommit ? 4 : 8, row.Bytes);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Equal(afterCommit ? 2 : 1, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        var beforeReplay = await fixture.CreateStage(fresh).ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.Equal(0, beforeReplay.Settled);
        var replay = await fixture.CreateStage(fresh).ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None);
        Assert.Equal(original.Key, replay.Key);
        Assert.Equal(original.ObjectId, replay.ObjectId);
        Assert.Equal(4, replay.SizeBytes);
        await fixture.CreateStage(fresh).SettlePhysicalAsync(replay, CancellationToken.None);
        Assert.Equal(StorageCapacityReservationStatuses.Released, (await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(original.AbsolutePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(3)]
    public async Task InvalidWrittenSizeCannotShrinkOrReleaseTheAdmittedReservation(int actualSize)
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var original = await fixture.ReserveAsync();
        if (actualSize is <= 0 or > 8)
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(original, actualSize, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(original, actualSize, CancellationToken.None));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(8, row.Bytes);
        Assert.Equal(original.BindingHash, row.BindingHash);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Equal(1, await fixture.Db.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
    }

    [Theory]
    [InlineData("object")]
    [InlineData("binding")]
    [InlineData("path")]
    public async Task ChangedReceiptCannotConfirmAnOwnedReservation(string change)
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var original = await fixture.ReserveAsync();
        var altered = change switch
        {
            "object" => original with { ObjectId = Guid.NewGuid() },
            "binding" => original with { BindingHash = new string('a', 64) },
            _ => original with { AbsolutePath = Path.Combine(fixture.Root, "outside.tmp") }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(altered, 4, CancellationToken.None));
        Assert.Equal(8, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(original.AbsolutePath));
    }

    [Fact]
    public async Task ConfirmedCompletionCannotBeResizedThroughTheReturnedReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var original = await fixture.ReserveAsync();
        var confirmed = await fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None);
        await File.WriteAllBytesAsync(original.AbsolutePath, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(confirmed, 3, CancellationToken.None));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(4, row.Bytes);
        Assert.Equal(confirmed.BindingHash, row.BindingHash);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
    }

    [Fact]
    public async Task EqualSizeConfirmationAlsoRecordsImmutableCompletion()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var original = await fixture.ReserveAsync();
        await File.WriteAllBytesAsync(original.AbsolutePath, new byte[8]);
        var confirmed = await fixture.Stage.ConfirmPhysicalSizeAsync(original, 8, CancellationToken.None);
        Assert.Equal(original.Key, confirmed.Key);
        Assert.Equal(8, confirmed.SizeBytes);
        Assert.NotEqual(original.BindingHash, confirmed.BindingHash);
        await File.WriteAllBytesAsync(original.AbsolutePath, [1, 2, 3, 4]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(confirmed, 4, CancellationToken.None));
        Assert.Equal(8, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Bytes);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("version")]
    [InlineData("size")]
    public async Task ChangedCompletionReceiptBlocksRepeatAndAutomaticDebtRelease(string change)
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var original = await fixture.ReserveAsync();
        await fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None);
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        var context = System.Text.Json.Nodes.JsonNode.Parse(row.ProductContextJson!)!;
        if (change == "version") context["Version"] = 2;
        if (change == "size") context["ActualBytes"] = 3;
        var changed = change == "json" ? "{broken" : context.ToJsonString();
        var old = DateTime.UtcNow.AddHours(-1);
        await fixture.Db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.ProductContextJson, changed).SetProperty(item => item.UpdatedAt, old));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None));
        var recovered = await fixture.Stage.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.Equal(0, recovered.Settled);
        Assert.Equal(1, recovered.Retained);
        Assert.Equal("invalidAuthority", Assert.Single(recovered.Items).Status);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(original.AbsolutePath));
    }

    [Fact]
    public async Task ExhaustedBudgetEpochRollsBackConfirmedSizeAndPreservesBytes()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var original = await fixture.ReserveAsync();
        await fixture.Db.StorageCapacityEpochs.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, long.MaxValue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stage.ConfirmPhysicalSizeAsync(original, 4, CancellationToken.None));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(8, row.Bytes);
        Assert.Equal(original.BindingHash, row.BindingHash);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(original.AbsolutePath));
    }

    private sealed class CommitFault(bool afterCommit, int ignoredCommits = 0) : DbTransactionInterceptor
    {
        private int _commits;
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && ++_commits > ignoredCommits && !afterCommit) { Armed = false; throw new IOException("Owned before-commit fault"); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && _commits > ignoredCommits && afterCommit) { Armed = false; throw new IOException("Owned lost commit acknowledgement"); }
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection? _sqlite;
        private readonly NpgsqlConnection? _admin;
        private readonly string? _name;
        private readonly string? _postgresConnection;
        private readonly IInterceptor[] _interceptors;
        private readonly List<StorageStageCapacityTicket> _ownedWriterTickets = [];
        public string Root { get; }
        public PaymentContext Db { get; private set; } = null!;
        public StorageStageCapacityService Stage => CreateStage(Db);
        private readonly TestEnvironment _environment;
        private readonly IConfiguration _configuration;
        private readonly StaticOptions _options = new(new StorageOrchestrationOptions { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 });
        private Fixture(string root, SqliteConnection? sqlite, NpgsqlConnection? admin, string? name, string? postgresConnection, IInterceptor[] interceptors)
        {
            Root = root; _sqlite = sqlite; _admin = admin; _name = name; _postgresConnection = postgresConnection; _interceptors = interceptors;
            _environment = new(root);
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [ElectronicSignatureStoragePaths.StorageRootConfigurationKey] = Path.Combine(root, "signatures") }).Build();
        }
        public static async Task<Fixture> CreateAsync(bool postgres, params IInterceptor[] interceptors)
        {
            var root = Path.Combine(Path.GetTempPath(), "pp-stage-physical-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "signatures"));
            SqliteConnection? sqlite = null; NpgsqlConnection? admin = null; string? name = null; string? connectionString = null;
            if (postgres)
            {
                var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                Assert.False(string.IsNullOrWhiteSpace(raw), "Local PostgreSQL is required for this qualification.");
                var settings = new NpgsqlConnectionStringBuilder(raw!) { Database = "postgres", Pooling = false };
                Assert.Contains(settings.Host, new[] { "127.0.0.1", "::1", "localhost" });
                admin = new(settings.ConnectionString); await admin.OpenAsync();
                name = "pp_stage_physical_" + Guid.NewGuid().ToString("N");
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await create.ExecuteNonQueryAsync();
                Console.WriteLine("Owned physical stage database: " + name);
                settings.Database = name; connectionString = settings.ConnectionString;
            }
            else { sqlite = new("Data Source=:memory:"); await sqlite.OpenAsync(); }
            var fixture = new Fixture(root, sqlite, admin, name, connectionString, interceptors);
            try
            {
                fixture.Db = fixture.NewContext();
                if (postgres) await fixture.Db.Database.MigrateAsync(); else await fixture.Db.Database.EnsureCreatedAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public PaymentContext NewContext()
        {
            if (_postgresConnection is not null)
            {
                var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(_postgresConnection);
                builder.AddInterceptors(_interceptors);
                return new PostgreSqlPaymentContext(builder.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(_sqlite!);
            sqlite.AddInterceptors(_interceptors);
            return new PaymentContext(sqlite.Options);
        }
        public StorageStageCapacityService CreateStage(PaymentContext context) => new(context,
            new StorageProviderRegistry([new LegacyLocalStorageProvider(_environment, _configuration)]), _options, _environment, _configuration);
        public async Task<StorageStageCapacityTicket> ReserveAsync()
        {
            var path = Path.Combine(Root, "signatures", "original.tmp");
            var (ticket, error) = await Stage.ReserveCompatibilityAsync("esign:physical-size", path, 8, "esign-current", CancellationToken.None);
            Assert.Null(error); Assert.NotNull(ticket);
            _ownedWriterTickets.Add(ticket);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            return ticket;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var ticket in _ownedWriterTickets) await ticket.CloseWriterAsync();
            if (Db is not null) await Db.DisposeAsync();
            if (_sqlite is not null) await _sqlite.DisposeAsync();
            if (_admin is not null && _name is not null)
            {
                Assert.Matches("^pp_stage_physical_[a-f0-9]{32}$", _name);
                long count = -1;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=@name", _admin);
                    query.Parameters.AddWithValue("name", _name); count = Convert.ToInt64(await query.ExecuteScalarAsync());
                    if (count == 0) break; await Task.Delay(100);
                }
                Assert.Equal(0L, count);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\"", _admin); await drop.ExecuteNonQueryAsync();
                await _admin.DisposeAsync();
            }
            Assert.Matches("^pp-stage-physical-[a-f0-9]{32}$", Path.GetFileName(Root));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class StaticOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
