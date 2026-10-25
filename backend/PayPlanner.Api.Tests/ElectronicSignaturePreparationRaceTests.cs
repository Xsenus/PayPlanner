using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignaturePreparationRaceTests : IClassFixture<PostgreSqlMigratedTemplate>
{
    private readonly PostgreSqlMigratedTemplate postgresTemplate;

    public ElectronicSignaturePreparationRaceTests(PostgreSqlMigratedTemplate postgresTemplate)
        => this.postgresTemplate = postgresTemplate;

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "other-unique")]
    [InlineData(true, "other-unique")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    public async Task WinnerAfterInitialLookupReturnsCanonicalEnvelopeOnlyAfterExactCollisionAndConfirmedRollback(bool postgres, string fault)
    {
        var name = "pp_esign_race_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        PostgreSqlMigratedTemplate.DatabaseLease? lease = null;
        try
        {
            var barrier = new WinnerBeforeTransaction { FailRollback = fault == "rollback" };
            DbContextOptionsBuilder normal = postgres ? new DbContextOptionsBuilder<PostgreSqlPaymentContext>() : new DbContextOptionsBuilder<PaymentContext>();
            DbContextOptionsBuilder racing = postgres ? new DbContextOptionsBuilder<PostgreSqlPaymentContext>() : new DbContextOptionsBuilder<PaymentContext>();
            if (postgres)
            {
                lease = await postgresTemplate.CreateDatabaseAsync("pp_esign_race_");
                normal.UseNpgsql(lease.ConnectionString);
                racing.UseNpgsql(lease.ConnectionString);
            }
            else
            {
                var connection = $"Data Source={Path.Combine(root, "race.db")};Pooling=False";
                normal.UseSqlite(connection);
                racing.UseSqlite(connection);
            }
            normal.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            racing.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).AddInterceptors(barrier);
            PaymentContext Create(DbContextOptions options) => postgres
                ? new PostgreSqlPaymentContext((DbContextOptions<PostgreSqlPaymentContext>)options, useObjectAuditModel: false)
                : new PaymentContext(options, useObjectAuditModel: false);
            int userId;
            int clientId;
            await using (var setup = Create(normal.Options))
            {
                if (!postgres) await setup.Database.EnsureCreatedAsync();
                var user = new User
                {
                    Email = "race@example.test",
                    FullName = "Тест гонки",
                    PasswordHash = "hash",
                    Role = new Role { Name = "admin", Description = "Тест" },
                    IsActive = true,
                    IsApproved = true
                };
                var client = new Client { Name = "Синтетический клиент", IsActive = true };
                setup.AddRange(user, client);
                await setup.SaveChangesAsync();
                userId = user.Id;
                clientId = client.Id;
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ElectronicSignature:StorageRoot"] = Path.Combine(root, "esign"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var environment = new RaceEnvironment(root);
            var request = new ElectronicSignaturePreparation("invoice", 101, 1, clientId,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential,
                "KonturDiadoc", 10, false, userId, [new("Клиент", "client@example.test")]);
            var bytes = "%PDF-1.7 exact race input"u8.ToArray();
            ElectronicSignatureEnvelope? winner = null;
            barrier.RunWinner = async () =>
            {
                await using var db = Create(normal.Options);
                var service = new ElectronicSignatureService(db, configuration, protection, environment);
                winner = await service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
                if (fault == "other-unique")
                {
                    Exception cause = postgres
                        ? new PostgresException("Owned unrelated unique fault", "ERROR", "ERROR", "23505",
                            constraintName: "IX_ElectronicSignatureEnvelopes_PublicId")
                        : new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed: ElectronicSignatureEnvelopes.PublicId", 19, 2067);
                    throw new DbUpdateException("Owned unrelated unique fault", cause);
                }
            };
            await using (var db = Create(racing.Options))
            {
                var service = new ElectronicSignatureService(db, configuration, protection, environment);
                if (fault == "none")
                {
                    var result = await service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
                    Assert.NotNull(winner);
                    Assert.Equal(winner.Id, result.Id);
                    Assert.Equal(winner.PublicId, result.PublicId);
                    Assert.Equal(winner.Signers.Single().Id, result.Signers.Single().Id);
                    Assert.Equal(winner.SnapshotPath, result.SnapshotPath);
                }
                else
                {
                    await Assert.ThrowsAsync<DbUpdateException>(() => service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None));
                }
                Assert.NotNull(winner);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(winner.SnapshotPath));
                Assert.DoesNotContain(db.ChangeTracker.Entries<ElectronicSignatureEnvelope>(), e => e.State == EntityState.Added);
            }
            await using (var verification = Create(normal.Options))
            {
                Assert.Single(await verification.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
                Assert.Single(await verification.ElectronicSignatureSigners.AsNoTracking().ToArrayAsync());
                Assert.Single(await verification.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
            }
            Assert.Equal(fault == "rollback" ? 2 : 1,
                Directory.GetFiles(Path.Combine(root, "esign"), "*.pdf", SearchOption.AllDirectories).Length);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "esign"), "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class WinnerBeforeTransaction : DbTransactionInterceptor
    {
        public Func<Task>? RunWinner { get; set; }
        public bool FailRollback { get; set; }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (FailRollback)
            {
                FailRollback = false;
                throw new IOException("Owned unconfirmed rollback fault.");
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var run = RunWinner;
            RunWinner = null;
            if (run is not null) await run();
            return result;
        }
    }

    private sealed class RaceEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
