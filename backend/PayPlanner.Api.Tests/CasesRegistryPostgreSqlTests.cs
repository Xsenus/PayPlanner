using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CasesRegistryPostgreSqlTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_cases_registry_test_";

    [Fact]
    public async Task Get_CombinesActivitySourcesInOnePostgreSqlQuery()
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
            var interceptor = new RecordingCommandInterceptor();
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .AddInterceptors(interceptor)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            await db.Database.MigrateAsync();

            var role = new Role { Name = "registry-reader", Description = "registry-reader" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });
            var user = new User
            {
                Email = "postgres-registry@example.com",
                PasswordHash = "hash",
                FullName = "Пользователь реестра",
                RoleId = role.Id,
                IsActive = true,
                IsApproved = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.ClientCases.Add(new ClientCase
            {
                Client = new Client
                {
                    Name = "Клиент PostgreSQL",
                    IsActive = true,
                    ResponsibleUserId = user.Id,
                },
                Title = "Дело PostgreSQL",
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = user.Id,
            });
            await db.SaveChangesAsync();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                "test"));
            var controller = new CasesV2Controller(db)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = principal },
                },
            };
            interceptor.Commands.Clear();

            var result = await controller.Get(
                clientId: null,
                legalEntityId: null,
                status: null,
                search: null,
                responsibleUserId: null,
                caseType: null,
                priority: null,
                from: null,
                to: null,
                minFee: null,
                maxFee: null,
                sortBy: null,
                sortDir: null,
                page: 1,
                pageSize: 50,
                ct: CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            var activityCommand = Assert.Single(interceptor.Commands, command =>
                command.Contains("TaskJournalComments", StringComparison.Ordinal)
                && command.Contains("ClientCaseDocuments", StringComparison.Ordinal)
                && command.Contains("ClientCaseCourtEvents", StringComparison.Ordinal));
            Assert.Contains("UNION ALL", activityCommand, StringComparison.OrdinalIgnoreCase);
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
