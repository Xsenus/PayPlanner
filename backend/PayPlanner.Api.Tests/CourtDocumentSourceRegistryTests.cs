using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Text;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtDocumentSourceRegistryTests(
    PostgreSqlMigratedTemplate postgresTemplate,
    SqliteMigratedTemplate sqliteTemplate)
    : IClassFixture<PostgreSqlMigratedTemplate>, IClassFixture<SqliteMigratedTemplate>
{
    private Task<Fixture> CreateFixtureAsync(bool postgres, bool migrateFromEmpty = false)
        => Fixture.CreateAsync(postgres, postgresTemplate, sqliteTemplate, migrateFromEmpty);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoftDeletedCaseRetainsCourtEvidenceButBlocksPublicationUntilRestored(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var receipt = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [Input()], default)).Sources);
        var source = Publication(receipt);
        var original = await registry.ReadContentAsync(source, default);
        Assert.NotNull(original);
        var clientCase = await f.Db.ClientCases.SingleAsync(c => c.Id == f.CaseId);
        f.Db.Entry(clientCase).Property<bool>("IsDeleted").CurrentValue = true;
        await f.Db.SaveChangesAsync();

        await using var observer = f.NewContext();
        Assert.Empty(await observer.ClientCases.ToArrayAsync());
        Assert.Equal(1, await observer.CourtDocumentSourceScopes.CountAsync());
        Assert.Equal(1, await observer.CourtDocumentSources.CountAsync());
        var retained = await observer.CourtDocumentSourceAttempts.AsNoTracking()
            .Include(a => a.Source).ThenInclude(s => s.Scope).SingleAsync();
        Assert.Equal(receipt.Snapshot.AttemptId, retained.Id);
        Assert.Equal(receipt.Generation.FrozenDomainJson, retained.FrozenSnapshotJson);
        Assert.Equal(f.CaseId, retained.Source.Scope.ClientCaseId);
        Assert.Equal(original, await observer.CourtDocumentSourceContents.Select(c => c.Bytes).SingleAsync());
        var restarted = new CourtDocumentSourceRegistry(observer);
        Assert.False(await restarted.CanPublishAsync(source, default));
        Assert.Null(await restarted.ReadContentAsync(source, default));
        Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.False(observer.Database.HasPendingModelChanges());

        f.Db.Entry(clientCase).Property<bool>("IsDeleted").CurrentValue = false;
        await f.Db.SaveChangesAsync();
        Assert.True(await restarted.CanPublishAsync(source, default));
        Assert.Equal(original, await restarted.ReadContentAsync(source, default));
        Assert.Equal(1, await observer.CourtDocumentSourceAttempts.CountAsync());
    }

    private const string Binding = "https://owned-court.test/case/42";
    private static readonly DateTime CheckedAt = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    private static CourtDocumentSourceInput Input(char suffix = 'a') => new("sudrf-act-" + new string(suffix, 32),
        "court-summary.html", "Сводка судебного события", "https://owned-court.test/event/" + suffix,
        "<!doctype html><html lang=\"ru\"><body>Исходная сводка " + suffix + "</body></html>");
    private static ManagedCaseDocumentGenerationSource Publication(CourtDocumentSourceReceipt receipt) => new(
        receipt.Snapshot.CaseId, receipt.Snapshot.ActorId, receipt.Snapshot.AttemptId, receipt.Snapshot.DocumentKey,
        receipt.Snapshot.DocumentKey, receipt.Snapshot.FileName, "text/html; charset=utf-8",
        CourtSyncNormalization.DocumentTypes.CourtEventSummary, "received", receipt.Generation);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayRetainsOriginalAttemptAndRejectsRemovedOrStaleEvidence(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var first = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [Input(), Input()], default)).Sources);
        var replay = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.OtherActorId, "Второй автор",
            Binding, CheckedAt.AddSeconds(1), [Input() with { Title = "Новая подпись", FileName = "new.html",
                RenderedHtml = "<html>Изменённые сведения суда</html>" }], default)).Sources);
        Assert.Equal(first, replay);
        Assert.Equal(Encoding.UTF8.GetBytes(Input().RenderedHtml), await registry.ReadContentAsync(Publication(replay), default));
        Assert.True(await registry.CanPublishAsync(Publication(first), default));
        var conflict = await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор", Binding,
            CheckedAt.AddSeconds(2), [Input(), Input() with { Title = "Неоднозначная строка" }], default);
        Assert.Equal("conflict", conflict.Status);
        Assert.Equal(2, (await f.Db.CourtDocumentSourceScopes.AsNoTracking().SingleAsync()).Version);
        Assert.Equal("recorded", (await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt.AddSeconds(2), [], default)).Status);
        Assert.False(await registry.CanPublishAsync(Publication(first), default));
        Assert.Equal("stale", (await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt.AddSeconds(1), [Input()], default)).Status);
        Assert.Equal("stale", (await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt.AddSeconds(2), [Input()], default)).Status);
        Assert.False(await registry.CanPublishAsync(Publication(first), default));
        var recovered = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.OtherActorId, "Второй автор",
            Binding, CheckedAt.AddSeconds(3), [Input()], default)).Sources);
        Assert.Equal(first, recovered);
        Assert.True(await registry.CanPublishAsync(Publication(first), default));
        Assert.Equal(1, await f.Db.CourtDocumentSources.CountAsync());
        Assert.Equal(1, await f.Db.CourtDocumentSourceAttempts.CountAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationUsesFreshAuthorityAndStoredProofWithoutActorTakeover(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var receipt = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [Input()], default)).Sources);
        var source = Publication(receipt);
        Assert.False(await registry.CanPublishAsync(source with { ActorId = f.OtherActorId }, default));
        Assert.False(await registry.CanPublishAsync(source with { UploadKey = Guid.NewGuid() }, default));
        Assert.False(await registry.CanPublishAsync(source with
        {
            Generation = receipt.Generation with
            { FrozenDomainJson = receipt.Generation.FrozenDomainJson + " " }
        }, default));
        await using (var external = f.NewContext())
        {
            var actor = await external.Users.SingleAsync(u => u.Id == f.ActorId);
            actor.IsActive = false;
            await external.SaveChangesAsync();
        }
        Assert.False(await registry.CanPublishAsync(source, default));
        var replay = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.OtherActorId, "Второй автор",
            Binding, CheckedAt.AddSeconds(1), [Input()], default)).Sources);
        Assert.Equal(receipt, replay);
        Assert.False(await registry.CanPublishAsync(Publication(replay), default));
        await using (var external = f.NewContext())
        {
            var actor = await external.Users.SingleAsync(u => u.Id == f.ActorId); actor.IsActive = true;
            var courtCase = await external.ClientCases.SingleAsync(c => c.Id == f.CaseId); courtCase.CourtUrl = Binding + "/changed";
            await external.SaveChangesAsync();
        }
        Assert.False(await registry.CanPublishAsync(source, default));
        Assert.Equal("forbidden", (await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt.AddSeconds(2), [Input()], default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureDoesNotCommitUnrelatedPendingWork(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var pending = new Client { Name = "Незавершённая запись пользователя" };
        f.Db.Clients.Add(pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CourtDocumentSourceRegistry(f.Db)
            .RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Автор", Binding, CheckedAt, [Input()], default));
        Assert.Equal(EntityState.Added, f.Db.Entry(pending).State);
        await using var observer = f.NewContext();
        Assert.False(await observer.Clients.AnyAsync(c => c.Name == pending.Name));
        Assert.Empty(await observer.CourtDocumentSourceScopes.ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentPostgresCapturesShareOneDurableAttempt()
    {
        await using var f = await CreateFixtureAsync(true);
        await using var other = f.NewContext();
        var results = await Task.WhenAll(
            new CourtDocumentSourceRegistry(f.Db).RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор", Binding, CheckedAt, [Input()], default),
            new CourtDocumentSourceRegistry(other).RecordAcceptedBatchAsync(f.CaseId, f.OtherActorId, "Второй автор", Binding, CheckedAt, [Input()], default));
        Assert.All(results, result => Assert.Equal("recorded", result.Status));
        Assert.Equal(Assert.Single(results[0].Sources), Assert.Single(results[1].Sources));
        Assert.Equal(1, await other.CourtDocumentSourceScopes.CountAsync());
        Assert.Equal(1, await other.CourtDocumentSources.CountAsync());
        Assert.Equal(1, await other.CourtDocumentSourceAttempts.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradePreservesExistingRecordsAndDowngradeProtectsClaims(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, migrateFromEmpty: true);
        await f.Db.Database.MigrateAsync(f.PreviousMigration);
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = f.CaseId,
            Title = "Сохранённый срок",
            DeadlineType = "appeal",
            DueAt = CheckedAt.AddDays(10),
            SourceType = "sudrf",
            SourceDocumentKey = Input().DocumentKey
        };
        f.Db.ClientCaseProceduralDeadlines.Add(deadline);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        await f.Db.Database.MigrateAsync();
        Assert.False(f.Db.Database.HasPendingModelChanges());
        var preserved = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        Assert.Equal(deadline.Id, preserved.Id);
        Assert.Equal(deadline.DueAt, preserved.DueAt);
        Assert.Equal(deadline.SourceDocumentKey, preserved.SourceDocumentKey);
        Assert.Empty(await f.Db.CourtDocumentSourceScopes.ToArrayAsync());
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var receipt = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [Input()], default)).Sources);
        if (postgres)
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => f.Db.Database.MigrateAsync(f.PreviousMigration));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.MigrateAsync(f.PreviousMigration));
            Assert.Equal(19, failure.SqliteErrorCode);
        }
        Assert.Equal(receipt.Snapshot.AttemptId, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).Id);
        Assert.True(await registry.CanPublishAsync(Publication(receipt), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceScopeCannotBeAttachedToAnotherCase(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var receipt = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [Input()], default)).Sources);
        var other = new ClientCase
        {
            Title = "Другое дело",
            CourtUrl = Binding,
            Client = new Client { Name = "Другой синтетический клиент" }
        };
        f.Db.ClientCases.Add(other); await f.Db.SaveChangesAsync();
        Assert.False(await registry.CanPublishAsync(Publication(receipt) with { CaseId = other.Id }, default));
        f.Db.CourtDocumentSources.Add(new CourtDocumentSource
        {
            ScopeId = (await f.Db.CourtDocumentSourceScopes.SingleAsync()).Id,
            ClientCaseId = other.Id,
            DocumentKey = Input('b').DocumentKey
        });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        if (postgres) Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        else Assert.Equal(19, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, await f.Db.CourtDocumentSources.CountAsync());
        Assert.True(await registry.CanPublishAsync(Publication(receipt), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveHistoryIsUnchangedUntilItsSourceReappears(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var first = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [Input()], default)).Sources);
        await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор", Binding, CheckedAt.AddSeconds(1), [], default);
        var second = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.OtherActorId, "Второй автор",
            Binding, CheckedAt.AddSeconds(2), [Input('b')], default)).Sources);
        var inactive = await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync(s => s.Id == first.Snapshot.SourceId);
        Assert.False(inactive.IsActive);
        Assert.Equal(2, inactive.AcceptedScopeVersion);
        var replay = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt.AddSeconds(3), [Input()], default)).Sources);
        Assert.Equal(first, replay);
        Assert.True(await registry.CanPublishAsync(Publication(first), default));
        Assert.False(await registry.CanPublishAsync(Publication(second), default));
        Assert.Equal(4, (await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync(s => s.Id == first.Snapshot.SourceId)).AcceptedScopeVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenUnicodeContentSurvivesRestartAndRejectsByteCorruption(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var input = Input() with { RenderedHtml = "<html><body>" + new string('Ж', 50000) + "\0Конец</body></html>" };
        var receipt = Assert.Single((await new CourtDocumentSourceRegistry(f.Db).RecordAcceptedBatchAsync(f.CaseId,
            f.ActorId, "Первый автор", Binding, CheckedAt, [input], default)).Sources);
        Assert.True(input.RenderedHtml.Length > 48000);
        Assert.True(receipt.Generation.FrozenDomainJson.Length < 48000);
        var original = Encoding.UTF8.GetBytes(input.RenderedHtml);
        await using (var restarted = f.NewContext())
        {
            var registry = new CourtDocumentSourceRegistry(restarted);
            Assert.Equal(original, await registry.ReadContentAsync(Publication(receipt), default));
            var content = await restarted.CourtDocumentSourceContents.SingleAsync();
            var corrupted = content.Bytes.ToArray(); corrupted[0] ^= 1;
            content.Bytes = corrupted;
            await restarted.SaveChangesAsync();
        }
        Assert.Null(await new CourtDocumentSourceRegistry(f.Db).ReadContentAsync(Publication(receipt), default));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentLimitIsMeasuredInUtf8BytesAndDoesNotCreatePartialClaims(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var input = Input() with { RenderedHtml = new string('Ж', 25 * 1024 * 1024 / 2 + 1) };
        Assert.True(input.RenderedHtml.Length < 25 * 1024 * 1024);
        var result = await new CourtDocumentSourceRegistry(f.Db).RecordAcceptedBatchAsync(f.CaseId, f.ActorId,
            "Первый автор", Binding, CheckedAt, [input], default);
        Assert.Equal("invalid", result.Status);
        Assert.Empty(await f.Db.CourtDocumentSourceScopes.ToArrayAsync());
        Assert.Empty(await f.Db.CourtDocumentSourceAttempts.ToArrayAsync());
        Assert.Empty(await f.Db.CourtDocumentSourceContents.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureFailureRollsBackContentAndCanRetryInTheSameCleanContext(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var interceptor = new AfterContentSaveInterceptor(() => throw new IOException("Owned court capture failure"));
        await using var capture = f.NewContext(interceptor);
        var registry = new CourtDocumentSourceRegistry(capture);
        var large = Input() with { RenderedHtml = "<html>" + new string('x', 9 * 1024 * 1024) + "</html>" };
        await Assert.ThrowsAsync<IOException>(() => registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId,
            "Первый автор", Binding, CheckedAt, [large, Input('b'), Input('c')], default));
        Assert.True(interceptor.Triggered);
        Assert.False(capture.ChangeTracker.HasChanges());
        Assert.Empty(await f.Db.CourtDocumentSourceScopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CourtDocumentSourceAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CourtDocumentSourceContents.AsNoTracking().ToArrayAsync());
        var result = await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId,
            "Первый автор", Binding, CheckedAt, [large, Input('b'), Input('c')], default);
        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(3, await f.Db.CourtDocumentSourceContents.CountAsync());
        var receipt = Assert.Single(result.Sources, r => r.Snapshot.DocumentKey == large.DocumentKey);
        Assert.Equal(Encoding.UTF8.GetBytes(large.RenderedHtml), await registry.ReadContentAsync(Publication(receipt), default));
    }

    private sealed class AfterContentSaveInterceptor(Action fail) : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Triggered && eventData.Context!.ChangeTracker.Entries<CourtDocumentSourceContent>().Any())
            {
                Triggered = true;
                fail();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection? sqlite;
        private SqliteMigratedTemplate.DatabaseLease? sqliteLease;
        private PostgreSqlMigratedTemplate.DatabaseLease? postgresLease;
        private NpgsqlConnection? admin;
        private string? databaseName;
        private string? connectionString;
        public PaymentContext Db { get; private set; } = null!;
        public int CaseId { get; private set; }
        public int ActorId { get; private set; }
        public int OtherActorId { get; private set; }
        public string PreviousMigration { get; private set; } = null!;
        public PaymentContext NewContext(params IInterceptor[] interceptors) => sqlite is not null
            ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite).UsePayPlannerSqliteMigrations()
                .AddInterceptors(interceptors).Options)
            : new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString)
                .AddInterceptors(interceptors).Options);
        public static async Task<Fixture> CreateAsync(bool postgres,
            PostgreSqlMigratedTemplate postgresTemplate, SqliteMigratedTemplate sqliteTemplate,
            bool migrateFromEmpty)
        {
            var f = new Fixture();
            try
            {
                if (postgres && !migrateFromEmpty)
                {
                    f.postgresLease = await postgresTemplate.CreateDatabaseAsync("pp_court_sources_");
                    f.connectionString = f.postgresLease.ConnectionString;
                }
                else if (postgres)
                {
                    var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                    Assert.False(string.IsNullOrWhiteSpace(raw));
                    var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
                    Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
                    f.admin = new NpgsqlConnection(settings.ConnectionString); await f.admin.OpenAsync();
                    var name = "pp_court_sources_" + Guid.NewGuid().ToString("N");
                    await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", f.admin)) await create.ExecuteNonQueryAsync();
                    f.databaseName = name;
                    settings.Database = name; f.connectionString = settings.ConnectionString;
                }
                else if (migrateFromEmpty)
                {
                    f.sqlite = new SqliteConnection("Data Source=:memory:");
                    await f.sqlite.OpenAsync();
                }
                else
                {
                    f.sqliteLease = await sqliteTemplate.CreateDatabaseAsync();
                    f.sqlite = f.sqliteLease.Connection;
                }
                f.Db = f.NewContext();
                var migrations = f.Db.Database.GetMigrations().ToArray();
                var claims = Assert.Single(migrations, m => m.Contains("_AddCourtDocumentSourceClaims", StringComparison.Ordinal));
                f.PreviousMigration = migrations[Array.IndexOf(migrations, claims) - 1];
                if (migrateFromEmpty) await f.Db.Database.MigrateAsync();
                Assert.Equal(migrations, (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray());
                Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
                Assert.False(f.Db.Database.HasPendingModelChanges());
                var email = await f.Db.SystemSettings.SingleOrDefaultAsync(s => s.Key == "notifications.email.enabled");
                if (email is null) f.Db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else email.Value = "false";
                var role = new Role
                {
                    Name = "owned-court-source-editor",
                    Permissions = [new RolePermission
                    { Section = RolePermissionCatalog.Cases, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All }]
                };
                var first = new User
                {
                    Email = "first@example.local",
                    FullName = "Первый автор",
                    PasswordHash = "synthetic",
                    IsActive = true,
                    IsApproved = true,
                    Role = role
                };
                var second = new User
                {
                    Email = "second@example.local",
                    FullName = "Второй автор",
                    PasswordHash = "synthetic",
                    IsActive = true,
                    IsApproved = true,
                    Role = role
                };
                f.Db.Users.AddRange(first, second);
                var courtCase = new ClientCase
                {
                    Client = new Client { Name = "Синтетический клиент", ResponsibleUser = first },
                    ResponsibleUser = first,
                    Title = "Синтетическое судебное дело",
                    CourtUrl = Binding
                };
                f.Db.ClientCases.Add(courtCase);
                await f.Db.SaveChangesAsync();
                f.CaseId = courtCase.Id; f.ActorId = first.Id; f.OtherActorId = second.Id;
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
            if (sqliteLease is not null) await sqliteLease.DisposeAsync();
            else if (sqlite is not null) await sqlite.DisposeAsync();
            if (postgresLease is not null) await postgresLease.DisposeAsync();
            if (admin is not null)
            {
                if (databaseName is not null)
                {
                    Assert.Matches("^pp_court_sources_[a-f0-9]{32}$", databaseName);
                    await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin); await drop.ExecuteNonQueryAsync();
                }
                await admin.DisposeAsync();
            }
        }
    }
}
