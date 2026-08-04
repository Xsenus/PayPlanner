using System.Data.Common;
using System.Reflection;
using System.Text.Json;
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

public sealed class ClientPassportControllerTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_passport_test_";

    [Fact]
    public void ControllerRequiresClientViewAndEditPermissions()
    {
        Assert.NotNull(typeof(ClientPassportController).GetCustomAttribute<AuthorizeAttribute>());
        var view = Assert.Single(typeof(ClientPassportController).GetCustomAttributes<RequireSectionPermissionAttribute>());
        Assert.Equal(RolePermissionCatalog.Clients, view.Section);
        Assert.Equal(RequireSectionPermissionOperations.View, view.Operation);

        var upsert = typeof(ClientPassportController).GetMethod(nameof(ClientPassportController.Upsert));
        var edit = Assert.Single(upsert!.GetCustomAttributes<RequireSectionPermissionAttribute>());
        Assert.Equal(RolePermissionCatalog.Clients, edit.Section);
        Assert.Equal(RequireSectionPermissionOperations.Edit, edit.Operation);
    }

    [Fact]
    public async Task GetReturnsNotFoundWithSingleQueryForMissingClient()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Controller.Get(404, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Single(fixture.Recorder.Commands);
    }

    [Fact]
    public async Task GetReturnsNoContentWithSingleQueryWhenPassportIsMissing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = await fixture.AddClientAsync();
        fixture.Recorder.Commands.Clear();

        var result = await fixture.Controller.Get(client.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Single(fixture.Recorder.Commands);
    }

    [Fact]
    public async Task GetProjectsOnlyPublicFieldsWithSingleQuery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = await fixture.AddClientAsync();
        fixture.Db.ClientPassports.Add(new ClientPassport
        {
            ClientId = client.Id,
            Series = "1234",
            Number = "567890",
            SeriesEncrypted = "cipher-series",
            SeriesBlindIndex = "blind-series",
            NumberEncrypted = "cipher-number",
            NumberBlindIndex = "blind-number",
            IsPrimary = true,
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Recorder.Commands.Clear();

        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Get(client.Id, CancellationToken.None));
        var response = Assert.IsType<ClientPassportResponse>(result.Value);

        Assert.Equal("1234", response.Series);
        Assert.Equal("567890", response.Number);
        var command = Assert.Single(fixture.Recorder.Commands);
        Assert.DoesNotContain("Encrypted", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BlindIndex", command, StringComparison.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("cipher", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blind", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpsertLoadsTargetWithOneSelectAndNormalizesAllowedFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = await fixture.AddClientAsync();
        fixture.Db.ClientPassports.Add(new ClientPassport
        {
            ClientId = client.Id,
            Series = "old",
            Number = "old",
            IsPrimary = true,
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Recorder.Commands.Clear();

        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Upsert(
            client.Id,
            new UpsertClientPassportRequest
            {
                Series = " 1234 ",
                Number = " 567890 ",
                IssuedBy = " МВД России ",
                DepartmentCode = " 540-001 ",
                BirthPlacePassport = " Новосибирск ",
                BirthPlaceFias = " 54 ",
            },
            CancellationToken.None));

        var response = Assert.IsType<ClientPassportResponse>(result.Value);
        Assert.Equal("1234", response.Series);
        Assert.Equal("567890", response.Number);
        Assert.Equal("МВД России", response.IssuedBy);
        Assert.Equal("540-001", response.DepartmentCode);
        Assert.Equal("Новосибирск", response.BirthPlacePassport);
        Assert.Equal("54", response.BirthPlaceFias);
        Assert.Equal(1, fixture.Recorder.Commands.Count(command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, fixture.Recorder.Commands.Count(command => command.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task UpsertCreatesPassportWithOneLookupQuery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = await fixture.AddClientAsync();
        fixture.Recorder.Commands.Clear();

        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Upsert(
            client.Id,
            new UpsertClientPassportRequest { Series = "1234", Number = "567890" },
            CancellationToken.None));

        var response = Assert.IsType<ClientPassportResponse>(result.Value);
        Assert.True(response.Id > 0);
        Assert.True(response.IsPrimary);
        Assert.Equal(1, fixture.Recorder.Commands.Count(command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, fixture.Recorder.Commands.Count(command => command.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, await fixture.Db.ClientPassports.AsNoTracking().CountAsync(item => item.ClientId == client.Id));
    }

    [Fact]
    public async Task UpsertReturnsNotFoundWithSingleQueryForMissingClient()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Controller.Upsert(
            404,
            new UpsertClientPassportRequest { Series = "1234" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Single(fixture.Recorder.Commands);
        Assert.False(await fixture.Db.ClientPassports.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task PostgreSqlGetAndUpsertExecuteAgainstFreshDatabase()
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
            var client = new Client { Name = "PostgreSQL паспорт" };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            var controller = new ClientPassportController(db);

            var created = Assert.IsType<OkObjectResult>(await controller.Upsert(
                client.Id,
                new UpsertClientPassportRequest { Series = "1234", Number = "567890" },
                CancellationToken.None));
            Assert.IsType<ClientPassportResponse>(created.Value);
            db.ChangeTracker.Clear();
            var loaded = Assert.IsType<OkObjectResult>(await controller.Get(client.Id, CancellationToken.None));
            var response = Assert.IsType<ClientPassportResponse>(loaded.Value);
            Assert.Equal("1234", response.Series);
            Assert.Equal("567890", response.Number);
        }
        finally
        {
            await using var terminate = adminConnection.CreateCommand();
            terminate.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, PaymentContext db, RecordingCommandInterceptor recorder)
        {
            _connection = connection;
            Db = db;
            Recorder = recorder;
            Controller = new ClientPassportController(db);
        }

        public PaymentContext Db { get; }
        public RecordingCommandInterceptor Recorder { get; }
        public ClientPassportController Controller { get; }

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

        public async Task<Client> AddClientAsync()
        {
            var client = new Client { Name = "Тестовый клиент" };
            Db.Clients.Add(client);
            await Db.SaveChangesAsync();
            return client;
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

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
