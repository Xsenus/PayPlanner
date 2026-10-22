using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientReferenceDataControllerTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_ref_test_";

    [Fact]
    public void ControllerRequiresClientViewPermissionAndBoundedQueryParameters()
    {
        Assert.NotNull(typeof(ClientReferenceDataController).GetCustomAttribute<AuthorizeAttribute>());
        var permission = Assert.Single(typeof(ClientReferenceDataController).GetCustomAttributes<RequireSectionPermissionAttribute>());
        Assert.Equal(RolePermissionCatalog.Clients, permission.Section);
        Assert.Equal(RequireSectionPermissionOperations.View, permission.Operation);

        var searchProperty = typeof(ClientReferenceSearchQuery).GetProperty(nameof(ClientReferenceSearchQuery.Search));
        var limitProperty = typeof(ClientReferenceSearchQuery).GetProperty(nameof(ClientReferenceSearchQuery.Limit));
        Assert.Equal(100, searchProperty!.GetCustomAttribute<MaxLengthAttribute>()?.Length);
        var range = limitProperty!.GetCustomAttribute<RangeAttribute>();
        Assert.Equal(1, range?.Minimum);
        Assert.Equal(50, range?.Maximum);
    }

    [Fact]
    public async Task EmptySearchReturnsAtMostThirtyActiveItemsWithOneQueryPerDictionary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(40);
        var query = new ClientReferenceSearchQuery();

        await AssertEndpointAsync(() => fixture.Controller.GetOkin(query, CancellationToken.None), fixture.Recorder, 30);
        await AssertEndpointAsync(() => fixture.Controller.GetOkso(query, CancellationToken.None), fixture.Recorder, 30);
        await AssertEndpointAsync(() => fixture.Controller.GetOkpdtr(query, CancellationToken.None), fixture.Recorder, 30);
    }

    [Fact]
    public async Task SearchMatchesCodeOrNameExcludesInactiveAndHonorsLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.OksoDictionaryItems.AddRange(
            new OksoDictionaryItem { Code = "40.03.01", Name = "Юриспруденция", IsActive = true },
            new OksoDictionaryItem { Code = "40.04.01", Name = "Магистратура права", IsActive = true },
            new OksoDictionaryItem { Code = "99.99.99", Name = "Право архивное", IsActive = false },
            new OksoDictionaryItem { Code = "38.03.01", Name = "Экономика", IsActive = true });
        await fixture.Db.SaveChangesAsync();
        fixture.Recorder.Commands.Clear();

        var byCode = await GetItemsAsync(await fixture.Controller.GetOkso(
            new ClientReferenceSearchQuery { Search = "40.", Limit = 1 },
            CancellationToken.None));
        Assert.Single(byCode);
        Assert.StartsWith("40.", byCode[0].Code, StringComparison.Ordinal);
        Assert.Single(fixture.Recorder.Commands);
        Assert.Contains("LIMIT", fixture.Recorder.Commands[0], StringComparison.OrdinalIgnoreCase);

        fixture.Recorder.Commands.Clear();
        var byName = await GetItemsAsync(await fixture.Controller.GetOkso(
            new ClientReferenceSearchQuery { Search = "права", Limit = 10 },
            CancellationToken.None));
        Assert.Single(byName);
        Assert.DoesNotContain(byName, item => item.Code == "99.99.99");
        Assert.Single(fixture.Recorder.Commands);
    }

    [Fact]
    public async Task ProjectionSelectsOnlyCompactResponseFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(1);

        var result = await GetItemsAsync(await fixture.Controller.GetOkpdtr(
            new ClientReferenceSearchQuery { Search = "0001", Limit = 5 },
            CancellationToken.None));

        Assert.Single(result);
        var sql = Assert.Single(fixture.Recorder.Commands);
        Assert.Contains("\"Id\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Code\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Name\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostgreSqlSearchIsCaseInsensitiveForRussianNamesOnFreshDatabase()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = PostgreSqlDatabasePrefix + Guid.NewGuid().ToString("N");
        Assert.StartsWith(PostgreSqlDatabasePrefix, databaseName, StringComparison.Ordinal);
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            await db.Database.MigrateAsync();
            db.OksoDictionaryItems.AddRange(
                new OksoDictionaryItem { Code = "40.03.01", Name = "Правовой профиль" },
                new OksoDictionaryItem { Code = "40.04.01", Name = "Магистратура права" },
                new OksoDictionaryItem { Code = "99.99.99", Name = "Право архивное", IsActive = false });
            await db.SaveChangesAsync();

            var controller = new ClientReferenceDataController(db);
            var items = await GetItemsAsync(await controller.GetOkso(
                new ClientReferenceSearchQuery { Search = "ПРАВ", Limit = 10 },
                CancellationToken.None));

            Assert.Equal(2, items.Count);
            Assert.DoesNotContain(items, item => item.Code == "99.99.99");
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task AssertEndpointAsync(
        Func<Task<IActionResult>> action,
        RecordingCommandInterceptor recorder,
        int expectedCount)
    {
        recorder.Commands.Clear();
        var items = await GetItemsAsync(await action());
        Assert.Equal(expectedCount, items.Count);
        Assert.Single(recorder.Commands);
        Assert.Contains("LIMIT", recorder.Commands[0], StringComparison.OrdinalIgnoreCase);
    }

    private static Task<List<ClientReferenceItemResponse>> GetItemsAsync(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Task.FromResult(Assert.IsType<List<ClientReferenceItemResponse>>(ok.Value));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, PaymentContext db, RecordingCommandInterceptor recorder)
        {
            _connection = connection;
            Db = db;
            Recorder = recorder;
            Controller = new ClientReferenceDataController(db);
        }

        public PaymentContext Db { get; }
        public RecordingCommandInterceptor Recorder { get; }
        public ClientReferenceDataController Controller { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var recorder = new RecordingCommandInterceptor();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .AddInterceptors(recorder)
                .Options;
            var db = new PaymentContext(options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            recorder.Commands.Clear();
            return new Fixture(connection, db, recorder);
        }

        public async Task SeedAsync(int count)
        {
            for (var index = 1; index <= count; index++)
            {
                var code = index.ToString("D4");
                Db.OkinDictionaryItems.Add(new OkinDictionaryItem { Code = code, Name = $"ОКИН {index:D4}" });
                Db.OksoDictionaryItems.Add(new OksoDictionaryItem { Code = code, Name = $"ОКСО {index:D4}" });
                Db.OkpdtrDictionaryItems.Add(new OkpdtrDictionaryItem { Code = code, Name = $"Код ОКПДТР {index:D4}" });
            }

            Db.OkinDictionaryItems.Add(new OkinDictionaryItem { Code = "inactive-okin", Name = "Неактивный ОКИН", IsActive = false });
            Db.OksoDictionaryItems.Add(new OksoDictionaryItem { Code = "inactive-okso", Name = "Неактивный ОКСО", IsActive = false });
            Db.OkpdtrDictionaryItems.Add(new OkpdtrDictionaryItem { Code = "inactive-okpdtr", Name = "Неактивный ОКПДТР", IsActive = false });
            await Db.SaveChangesAsync();
            Recorder.Commands.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
