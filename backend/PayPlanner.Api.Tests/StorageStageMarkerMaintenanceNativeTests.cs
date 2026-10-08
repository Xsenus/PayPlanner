using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedMarkerMaintenanceUsesFreshDatabaseAndPreservesPendingDebtAndSourceBytes(bool postgres)
    {
        var failure = new StageReleaseCommitFault(false);
        var proof = new StageReleaseProofReadFault(failure, true);
        await using var fixture = await CreateWriterFixtureAsync(postgres, failure, proof);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "objects"));
        var original = Path.Combine(fixture.Root, "objects", "original.tmp");
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync(
            "marker:hosted-released", original, 4, default)).Ticket);
        StorageStageCapacityTicket? pending = null;
        try
        {
            failure.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.Stages.ReleaseAsync(ticket, default));
            await ticket.CloseWriterAsync();
            proof.Suppressed = true;
            Assert.Equal(1, proof.Interceptions);
            Assert.True(File.Exists(MarkerPath(fixture.Root, ticket)));
            var old = DateTime.UtcNow.AddHours(-12);
            await fixture.Db.StorageCapacityReservations.Where(row => row.IdempotencyKey == ticket.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReleasedAt, old).SetProperty(row => row.UpdatedAt, old));
            pending = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync(
                "marker:hosted-pending", Path.Combine(fixture.Root, "objects", "pending.tmp"), 4, default)).Ticket);
            await File.WriteAllBytesAsync(original, [9, 8]);
            var before = JsonSerializer.Serialize(await fixture.Db.StorageCapacityReservations.AsNoTracking()
                .OrderBy(row => row.IdempotencyKey).ToArrayAsync());
            var epoch = await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            Assert.Equal("false", await fixture.Db.SystemSettings.Where(row => row.Key == "notifications.email.enabled")
                .Select(row => row.Value).SingleAsync());
            var environment = new TestEnvironment(fixture.Root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LocalStoragePaths.RootConfigurationKey] = Path.Combine(fixture.Root, "objects"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var monitor = new StaticOptions(new()
            {
                Enabled = true,
                WorkerEnabled = true,
                WorkerPollSeconds = 300,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var signal = new MarkerMaintenanceSignal();
            var connection = fixture.Db.Database.GetConnectionString()!;
            var services = new ServiceCollection();
            services.AddSingleton<IWebHostEnvironment>(environment);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IOptionsMonitor<StorageOrchestrationOptions>>(monitor);
            services.AddSingleton<IStorageProviderRegistry>(new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ILogger<StorageOperationWorker>>(signal);
            services.AddScoped<PaymentContext>(_ => postgres
                ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection)
                    .Options, useObjectAuditModel: false)
                : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options));
            services.AddScoped<IStorageOperationProcessor, MarkerMaintenanceDirtyCopy>();
            services.AddScoped<IStorageStageWriterMarkerRecoveryService, StorageStageCapacityService>();
            services.AddSingleton<StorageStageMarkerMaintenance>();
            services.AddHostedService<StorageOperationWorker>();
            await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            {
                var worker = Assert.IsType<StorageOperationWorker>(Assert.Single(provider.GetServices<IHostedService>()));
                await worker.StartAsync(default);
                try { await signal.Removed.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                finally
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await worker.StopAsync(stop.Token);
                }
                Assert.Empty(signal.Errors);
            }
            Assert.False(File.Exists(MarkerPath(fixture.Root, ticket)));
            Assert.True(File.Exists(MarkerPath(fixture.Root, pending)));
            Assert.False(File.Exists(pending.AbsolutePath));
            Assert.Equal(new byte[] { 9, 8 }, await File.ReadAllBytesAsync(original));
            Assert.Equal(before, JsonSerializer.Serialize(await fixture.Db.StorageCapacityReservations.AsNoTracking()
                .OrderBy(row => row.IdempotencyKey).ToArrayAsync()));
            Assert.Equal(epoch, await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.Empty(await fixture.Db.Clients.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            await ticket.CloseWriterAsync();
            if (pending is not null) await pending.CloseWriterAsync();
        }
    }

    private sealed class MarkerMaintenanceDirtyCopy(PaymentContext db) : IStorageOperationProcessor
    {
        public Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
        {
            db.Clients.Add(new() { Name = "Owned unsaved copy scope" });
            Assert.True(db.ChangeTracker.HasChanges());
            return Task.FromResult(new StorageOperationBatchResult(0, 0, 0, 0, 0));
        }
    }

    private sealed class MarkerMaintenanceSignal : ILogger<StorageOperationWorker>
    {
        public TaskCompletionSource Removed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel >= LogLevel.Warning) Errors.Add(message);
            if (logLevel == LogLevel.Information && message.StartsWith("Storage worker removed 1 released stage markers", StringComparison.Ordinal))
                Removed.TrySetResult();
        }
    }
}
