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

public sealed partial class MessengerSnapshotPreparationRaceTests : IClassFixture<PostgreSqlMigratedTemplate>
{
    private readonly PostgreSqlMigratedTemplate postgresTemplate;

    public MessengerSnapshotPreparationRaceTests(PostgreSqlMigratedTemplate postgresTemplate)
        => this.postgresTemplate = postgresTemplate;

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "other-unique")]
    [InlineData(true, "other-unique")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    [InlineData(false, "checksum")]
    [InlineData(true, "checksum")]
    [InlineData(false, "token")]
    [InlineData(true, "token")]
    [InlineData(false, "message")]
    [InlineData(true, "message")]
    public async Task WinnerAfterInitialLookupReturnsCanonicalDeliveryOnlyAfterExactCollisionAndConfirmedRollback(bool postgres, string fault)
    {
        var name = "pp_msg_race_" + Guid.NewGuid().ToString("N");
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
                lease = await postgresTemplate.CreateDatabaseAsync("pp_msg_race_");
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
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = root,
                ["MessengerDocumentDelivery:Enabled"] = "true",
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var environment = new RaceEnvironment(root);
            MessengerDocumentDeliveryService Service(PaymentContext db) => new(db,
                new LocalCaseDocumentStorage(environment, configuration), protection, environment, configuration);
            int userId;
            int clientId;
            int bindingId;
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
                bindingId = (await Service(setup).CreateVerifiedBindingAsync(userId, clientId, null,
                    "telegram", "telegram-bot", "Клиент", "123456789", "admin-confirmed", default)).Id;
            }
            var bytes = "%PDF-1.7 exact messenger race input"u8.ToArray();
            Task<PreparedMessengerDocumentDelivery> Prepare(MessengerDocumentDeliveryService service) =>
                service.PrepareGeneratedDocumentAsync(userId, "invoice", 101, 1, clientId,
                    "invoice.pdf", "application/pdf", new MemoryStream(bytes), bytes.Length, bindingId, null, default);
            PreparedMessengerDocumentDelivery? winner = null;
            barrier.RunWinner = async () =>
            {
                await using var db = Create(normal.Options);
                winner = await Prepare(Service(db));
                switch (fault)
                {
                    case "cancelled": winner.Delivery.Status = MessengerDocumentDeliveryStatuses.Cancelled; break;
                    case "checksum": winner.Delivery.ChecksumSha256 = new string('0', 64); break;
                    case "token": winner.Delivery.ProtectedLinkToken = "broken-token"; break;
                    case "message": winner.Delivery.MessagePreview = "Другой запрос"; break;
                }
                await db.SaveChangesAsync();
                if (fault == "other-unique")
                {
                    Exception cause = postgres
                        ? new PostgresException("Owned unrelated unique fault", "ERROR", "ERROR", "23505",
                            constraintName: "IX_MessengerDocumentDeliveries_PublicId")
                        : new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed: MessengerDocumentDeliveries.PublicId", 19, 2067);
                    throw new DbUpdateException("Owned unrelated unique fault", cause);
                }
            };
            await using (var db = Create(racing.Options))
            {
                if (fault == "none")
                {
                    var result = await Prepare(Service(db));
                    Assert.NotNull(winner);
                    Assert.Equal(winner.Delivery.Id, result.Delivery.Id);
                    Assert.Equal(winner.Delivery.PublicId, result.Delivery.PublicId);
                    Assert.Equal(winner.Delivery.SnapshotPath, result.Delivery.SnapshotPath);
                    Assert.Equal(winner.Token, result.Token);
                }
                else await Assert.ThrowsAsync<DbUpdateException>(() => Prepare(Service(db)));
                Assert.NotNull(winner);
                var snapshot = Path.Combine(root, "messenger-document-deliveries", winner.Delivery.SnapshotPath);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot));
                Assert.DoesNotContain(db.ChangeTracker.Entries<MessengerDocumentDelivery>(), e => e.State == EntityState.Added);
                await db.SaveChangesAsync();
            }
            await using (var verification = Create(normal.Options))
                Assert.Single(await verification.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
            Assert.Equal(fault == "rollback" ? 2 : 1,
                Directory.GetFiles(Path.Combine(root, "messenger-document-deliveries"), "*.snapshot", SearchOption.AllDirectories).Length);
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
