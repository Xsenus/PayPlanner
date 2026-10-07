using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtDeadlineSourceIdentityMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradePreservesDeadlinesAndBackfillsOnlyUnambiguousTrustedSources(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, upgrade: true);
        var clientCase = OwnedCase("Owned court identity upgrade");
        f.Db.ClientCases.Add(clientCase);
        await f.Db.SaveChangesAsync();
        var canonical = "sudrf-act-" + new string('a', 32);
        var legacy = "sudrf-act-" + new string('b', 24);
        var duplicate = "sudrf-act-" + new string('c', 32);
        foreach (var row in new[] { ("canonical", canonical, "sudrf", "sudrf", 1),
            ("legacy", legacy, "sudrf", "sudrf", 1), ("duplicate-one", duplicate, "sudrf", "sudrf", 1),
            ("duplicate-two", duplicate, "sudrf", "sudrf", 2), ("invalid", "sudrf-act-" + new string('z', 32), "sudrf", "sudrf", 1),
            ("manual-deadline", "sudrf-act-" + new string('d', 32), "sudrf", "manual", 1),
            ("manual-document", "sudrf-act-" + new string('e', 32), "manual", "sudrf", 1) })
        {
            var document = new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = row.Item2,
                Version = row.Item5,
                OriginalFileName = row.Item1 + ".html",
                DocumentType = "Сводка судебного события",
                ContentType = "text/html",
                SourceType = row.Item3
            };
            f.Db.ClientCaseDocuments.Add(document);
            await f.Db.SaveChangesAsync();
            await InsertHistoricalDeadlineAsync(f.Db, Deadline(clientCase.Id, row.Item1, document.Id, row.Item4));
        }
        var otherCase = OwnedCase("Owned foreign court case"); f.Db.ClientCases.Add(otherCase); await f.Db.SaveChangesAsync();
        var foreignDocument = new ClientCaseDocument
        {
            ClientCaseId = otherCase.Id,
            DocumentKey = "sudrf-act-" + new string('f', 32),
            OriginalFileName = "foreign-case.html",
            ContentType = "text/html",
            SourceType = "sudrf"
        };
        f.Db.ClientCaseDocuments.Add(foreignDocument); await f.Db.SaveChangesAsync();
        await InsertHistoricalDeadlineAsync(f.Db, Deadline(clientCase.Id, "foreign-case", foreignDocument.Id));
        f.Db.ChangeTracker.Clear();
        await f.Db.Database.MigrateAsync();
        var rows = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync();
        Assert.Equal(8, rows.Length);
        Assert.Equal(canonical, Assert.Single(rows, r => r.Title == "canonical").SourceDocumentKey);
        Assert.Equal(legacy, Assert.Single(rows, r => r.Title == "legacy").SourceDocumentKey);
        Assert.All(rows.Where(r => r.Title != "canonical" && r.Title != "legacy"), r => Assert.Null(r.SourceDocumentKey));
        Assert.All(rows, r => { Assert.Equal("Owned source notes", r.Notes); Assert.NotNull(r.ClientCaseDocumentId); Assert.Equal(DeadlineAt, r.DueAt); });
        Assert.False(f.Db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDocumentIdentityPreventsDuplicateAndSurvivesOptionalLinkRemoval(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, upgrade: false);
        var clientCase = OwnedCase("Owned pending court identity");
        f.Db.ClientCases.Add(clientCase);
        await f.Db.SaveChangesAsync();
        var key = "sudrf-act-" + new string('a', 32);
        var first = Deadline(clientCase.Id, "first"); first.SourceDocumentKey = key;
        f.Db.ClientCaseProceduralDeadlines.Add(first);
        await f.Db.SaveChangesAsync();
        var duplicate = Deadline(clientCase.Id, "duplicate"); duplicate.SourceDocumentKey = key;
        f.Db.ClientCaseProceduralDeadlines.Add(duplicate);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        if (postgres) Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        else Assert.Equal(19, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
        f.Db.ChangeTracker.Clear();
        var preserved = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        var document = new ClientCaseDocument
        {
            ClientCaseId = clientCase.Id,
            DocumentKey = key,
            OriginalFileName = "owned-linked-summary.html",
            DocumentType = "Сводка судебного события",
            ContentType = "text/html",
            SourceType = "sudrf"
        };
        f.Db.ClientCaseDocuments.Add(document);
        await f.Db.SaveChangesAsync();
        preserved.ClientCaseDocumentId = document.Id;
        await f.Db.SaveChangesAsync();
        await f.Db.ClientCaseDocuments.Where(d => d.Id == document.Id).ExecuteDeleteAsync();
        f.Db.ChangeTracker.Clear();
        preserved = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        Assert.Null(preserved.ClientCaseDocumentId);
        Assert.Equal(key, preserved.SourceDocumentKey);
        Assert.Equal(first.Id, preserved.Id);
        Assert.False(f.Db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DowngradeCannotDiscardPendingCourtIdentity(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, upgrade: false);
        var clientCase = OwnedCase("Owned downgrade court identity");
        f.Db.ClientCases.Add(clientCase); await f.Db.SaveChangesAsync();
        var deadline = Deadline(clientCase.Id, "pending"); deadline.SourceDocumentKey = "sudrf-act-" + new string('a', 32);
        f.Db.ClientCaseProceduralDeadlines.Add(deadline); await f.Db.SaveChangesAsync();
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
        Assert.Equal(deadline.SourceDocumentKey, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).SourceDocumentKey);
        Assert.Contains(f.IdentityMigration, await f.Db.Database.GetAppliedMigrationsAsync());
    }

    private static readonly DateTime DeadlineAt = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
    private static ClientCase OwnedCase(string title) => new() { Title = title, Client = new Client { Name = title + " client" } };
    private static ClientCaseProceduralDeadline Deadline(int caseId, string title, int? documentId = null, string sourceType = "sudrf")
        => new()
        {
            ClientCaseId = caseId,
            Title = title,
            DeadlineType = "appeal",
            DueAt = DeadlineAt,
            ClientCaseDocumentId = documentId,
            SourceType = sourceType,
            Notes = "Owned source notes"
        };

    private static async Task InsertHistoricalDeadlineAsync(PaymentContext db, ClientCaseProceduralDeadline deadline)
    {
        var entityType = db.Model.FindEntityType(typeof(ClientCaseProceduralDeadline))!;
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var entry = db.Entry(deadline);
        var properties = entityType.GetProperties()
            .Where(p => p.Name != nameof(ClientCaseProceduralDeadline.Id) && p.Name != nameof(ClientCaseProceduralDeadline.SourceDocumentKey)).ToArray();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "INSERT INTO \"ClientCaseProceduralDeadlines\" (" + string.Join(",", properties.Select(p => "\"" + p.GetColumnName(table) + "\""))
            + ") VALUES (" + string.Join(",", properties.Select((_, i) => "@p" + i)) + ")";
        for (var i = 0; i < properties.Length; i++)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = "p" + i;
            var modelValue = entry.Property(properties[i].Name).CurrentValue;
            var converter = properties[i].GetTypeMapping().Converter;
            parameter.Value = (converter is null ? modelValue : converter.ConvertToProvider(modelValue)) ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        await db.Database.OpenConnectionAsync();
        try { await command.ExecuteNonQueryAsync(); }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection? sqlite;
        private NpgsqlConnection? admin;
        private string? databaseName;
        private bool databaseCreated;
        public PaymentContext Db { get; private set; } = null!;
        public string PreviousMigration { get; private set; } = null!;
        public string IdentityMigration { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(bool postgres, bool upgrade)
        {
            var f = new Fixture();
            try
            {
                if (postgres)
                {
                    var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                    Assert.False(string.IsNullOrWhiteSpace(raw), "Owned PostgreSQL tests require PAYPLANNER_TEST_POSTGRES.");
                    var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
                    f.admin = new NpgsqlConnection(settings.ConnectionString); await f.admin.OpenAsync();
                    f.databaseName = "pp_court_identity_" + Guid.NewGuid().ToString("N");
                    await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{f.databaseName}\"", f.admin)) await create.ExecuteNonQueryAsync();
                    f.databaseCreated = true;
                    settings.Database = f.databaseName;
                    f.Db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options);
                }
                else
                {
                    f.sqlite = new SqliteConnection("Data Source=:memory:"); await f.sqlite.OpenAsync();
                    f.Db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(f.sqlite).UsePayPlannerSqliteMigrations().Options);
                }
                var migrations = f.Db.Database.GetMigrations().ToArray();
                f.IdentityMigration = Assert.Single(migrations, m => m.Contains("_AddCourtDeadlineSourceIdentity", StringComparison.Ordinal));
                f.PreviousMigration = migrations[Array.IndexOf(migrations, f.IdentityMigration) - 1];
                await f.Db.Database.MigrateAsync(upgrade ? f.PreviousMigration : null);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
            if (sqlite is not null) await sqlite.DisposeAsync();
            if (admin is not null)
            {
                if (databaseCreated && databaseName is not null)
                {
                    Assert.Matches("^pp_court_identity_[a-f0-9]{32}$", databaseName);
                    await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin); await drop.ExecuteNonQueryAsync();
                }
                await admin.DisposeAsync();
            }
        }
    }
}
