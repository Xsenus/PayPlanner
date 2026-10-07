using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCasePublicationQueuePostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPageUsesScopedIndexAndCursorSurvivesPivotChanges(bool filtered)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
            return;
        }
        var name = "pp_case_admin_page_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await create.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            var capture = new PageQueryCapture();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).AddInterceptors(capture).Options);
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var storageObject = new StorageObject
            {
                DataClass = "case-document",
                OwnerType = "case-document",
                OwnerId = "synthetic-pg-case",
                LogicalName = "synthetic-pg.pdf",
                ContentType = "application/pdf"
            };
            var date = DateTime.UtcNow;
            var recent = Enumerable.Range(1, 3).Select(index => new StorageOperation
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
                StorageObject = storageObject,
                OperationType = StorageOperationTypes.PublishCaseDocument,
                Status = StorageOperationStatuses.DeadLetter,
                IdempotencyKey = $"synthetic-recent:{index}",
                CreatedAt = date,
                UpdatedAt = date,
            }).ToArray();
            db.StorageOperations.AddRange(recent);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "StorageOperations" ("Id", "StorageObjectId", "OperationType", "Status", "IdempotencyKey", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT md5('synthetic-foreign:' || i)::uuid, {storageObject.Id}, {StorageOperationTypes.Replicate}, {StorageOperationStatuses.Pending},
                  'synthetic-foreign:' || i, 0, TIMESTAMPTZ '2025-01-01 UTC' - make_interval(secs => i), TIMESTAMPTZ '2025-01-01 UTC'
                FROM generate_series(1, 20000) AS i
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "StorageOperations" ("Id", "StorageObjectId", "OperationType", "Status", "IdempotencyKey", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT md5('synthetic-history:' || i)::uuid, {storageObject.Id}, {StorageOperationTypes.PublishCaseDocument},
                  CASE WHEN i % 2 = 0 THEN {StorageOperationStatuses.DeadLetter} ELSE {StorageOperationStatuses.Succeeded} END,
                  'synthetic-history:' || i, 0, TIMESTAMPTZ '2025-01-01 UTC' - make_interval(secs => i), TIMESTAMPTZ '2025-01-01 UTC'
                FROM generate_series(1, 20000) AS i
                """);
            await db.Database.ExecuteSqlRawAsync("ANALYZE \"StorageOperations\"");
            var reader = new StorageCasePublicationQueueReader(db);
            var status = filtered ? StorageOperationStatuses.DeadLetter : null;
            var first = await reader.ReadAsync(status, null, 2, CancellationToken.None);
            Assert.Equal(recent.Reverse().Take(2).Select(item => item.Id), first.Items.Select(item => item.Id));
            Assert.Equal(1, capture.Count);
            Assert.All(first.Items, item => Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind));
            await db.Database.OpenConnectionAsync();
            await using (var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + capture.Sql,
                (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                foreach (var parameter in capture.Parameters) explain.Parameters.Add(parameter);
                var json = Assert.IsType<string>(await explain.ExecuteScalarAsync());
                Console.WriteLine(json);
                using var plan = JsonDocument.Parse(json);
                var nodes = Walk(plan.RootElement[0].GetProperty("Plan"))
                    .Where(node => node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "StorageOperations").ToArray();
                Assert.NotEmpty(nodes);
                Assert.All(nodes, node =>
                {
                    Assert.NotEqual("Seq Scan", node.GetProperty("Node Type").GetString());
                    Assert.Equal(filtered ? "IX_StorageOperations_OperationType_Status_CreatedAt_Id"
                        : "IX_StorageOperations_OperationType_CreatedAt_Id", node.GetProperty("Index Name").GetString());
                    Assert.InRange(node.GetProperty("Actual Rows").GetInt32(), 0, 3);
                });
            }
            recent[1].Status = StorageOperationStatuses.Succeeded;
            recent[1].UpdatedAt = date.AddDays(1);
            await db.SaveChangesAsync();
            var second = await reader.ReadAsync(status, first.NextCursor, 2, CancellationToken.None);
            Assert.Equal(recent[0].Id, second.Items[0].Id);
            db.StorageOperations.Remove(recent[1]);
            await db.SaveChangesAsync();
            var repeated = await reader.ReadAsync(status, first.NextCursor, 2, CancellationToken.None);
            Assert.Equal(second.Items.Select(item => item.Id), repeated.Items.Select(item => item.Id));
            Assert.Equal(3, capture.Count);
        }
        finally
        {
            Assert.Matches("^pp_case_admin_page_[a-f0-9]{32}$", name);
            if (created)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    await using var active = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                    active.Parameters.AddWithValue("name", name);
                    if ((long)(await active.ExecuteScalarAsync())! == 0) break;
                    if (DateTime.UtcNow >= until) throw new InvalidOperationException("Owned case queue test database still has active connections; preserved.");
                    await Task.Delay(100);
                }
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static IEnumerable<JsonElement> Walk(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) foreach (var descendant in Walk(child)) yield return descendant;
    }

    private sealed class PageQueryCapture : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public string Sql { get; private set; } = "";
        public NpgsqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command is NpgsqlCommand && command.CommandText.Contains("case-publication-admin-page", StringComparison.Ordinal))
            {
                Count++;
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(item => (NpgsqlParameter)((ICloneable)item).Clone()).ToArray();
            }
            return new ValueTask<DbDataReader>(result);
        }
    }
}
