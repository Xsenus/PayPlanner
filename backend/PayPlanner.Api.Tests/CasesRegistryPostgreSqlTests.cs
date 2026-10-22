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
using PayPlanner.Api.Services;
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
            db.RolePermissions.AddRange(
                new RolePermission
                {
                    RoleId = role.Id,
                    Section = RolePermissionCatalog.Cases,
                    CanView = true,
                    CanEdit = true,
                    ViewScope = DataVisibilityScope.Own,
                },
                new RolePermission
                {
                    RoleId = role.Id,
                    Section = RolePermissionCatalog.Payments,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    RoleId = role.Id,
                    Section = RolePermissionCatalog.Contracts,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
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
            var client = new Client
            {
                Name = "Клиент PostgreSQL",
                IsActive = true,
                ResponsibleUserId = user.Id,
            };
            var clientCase = new ClientCase
            {
                Client = client,
                Title = "Дело PostgreSQL",
                Description = "Не загружать в компактную сводку",
                EventHistory = "05.08.2026 09:00: Внутреннее сообщение: Проверка PostgreSQL. Автор: Пользователь реестра.",
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = user.Id,
                FeeAmount = 15000m,
                CourtName = "Суд PostgreSQL",
                CourtClaimAmount = 250000.50m,
                CourtParties =
                [
                    new ClientCaseCourtParty
                    {
                        DisplayName = "Ответчик PostgreSQL",
                        Side = "respondent",
                        ProcessRole = "Ответчик",
                    },
                ],
            };
            db.ClientCases.Add(clientCase);
            db.UserSettings.Add(new UserSetting
            {
                UserId = user.Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var contract = new Contract
            {
                Number = "PG-1",
                Title = "Договор PostgreSQL",
                Date = DateTime.UtcNow.Date,
                Amount = 15000m,
                Status = ContractStatus.Concluded,
            };
            var invoice = new Payment
            {
                Client = client,
                ClientCase = clientCase,
                Contract = contract,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Account = "PG-1",
                Amount = 15000m,
                PaidAmount = 0m,
                Date = DateTime.UtcNow.Date,
                PlannedDate = DateTime.UtcNow.Date,
            };
            var payment = new Payment
            {
                Client = client,
                ClientCase = clientCase,
                Contract = contract,
                DocumentKind = DocumentKind.Payment,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 5000m,
                PaidAmount = 5000m,
                Date = DateTime.UtcNow.Date,
                PlannedDate = DateTime.UtcNow.Date,
                PaidDate = DateTime.UtcNow.Date,
            };
            db.Payments.AddRange(invoice, payment);
            await db.SaveChangesAsync();
            db.ContractInvoices.Add(new ContractInvoice { ContractId = contract.Id, InvoiceId = invoice.Id });
            db.ContractPayments.Add(new ContractPayment { ContractId = contract.Id, PaymentId = payment.Id });
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

            var amountFiltered = await CreateController(db, principal).GetCourtReportOverview(
                ct: CancellationToken.None, minClaimAmount: 250000.50m, maxClaimAmount: 250000.50m);
            Assert.Equal(1, amountFiltered.Value!.Total);
            Assert.Equal(250000.50m, amountFiltered.Value.Amounts.ClaimTotal);
            var amountExcluded = await CreateController(db, principal).GetCourtReportOverview(
                ct: CancellationToken.None, minClaimAmount: 250000.51m);
            Assert.Equal(0, amountExcluded.Value!.Total);

            interceptor.Commands.Clear();
            var timelineResult = await CreateController(db, principal).GetTimeline(clientCase.Id, CancellationToken.None);
            Assert.IsType<OkObjectResult>(timelineResult.Result);
            Assert.Equal(2, interceptor.Commands.Count);
            Assert.Contains("UserSettings", interceptor.Commands[^1], StringComparison.OrdinalIgnoreCase);

            interceptor.Commands.Clear();
            var partiesResult = await CreateController(db, principal).GetCourtParties(
                clientCase.Id,
                includeArchived: false,
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(partiesResult.Result);
            Assert.Equal(2, interceptor.Commands.Count);

            interceptor.Commands.Clear();
            var summaryResult = await CreateController(db, principal).GetSummary(clientCase.Id, CancellationToken.None);
            Assert.IsType<OkObjectResult>(summaryResult.Result);
            Assert.DoesNotContain(
                interceptor.Commands,
                command => command.Contains("Description", StringComparison.OrdinalIgnoreCase));

            interceptor.Commands.Clear();
            var financeResult = await CreateController(db, principal).GetFinance(clientCase.Id, CancellationToken.None);
            Assert.IsType<OkObjectResult>(financeResult.Result);
            // Five permission lookups and one bounded query each for the case,
            // payments, contracts and user time zone. No per-item contract loads.
            Assert.Equal(9, interceptor.Commands.Count);
            var linkedContractsCommand = Assert.Single(interceptor.Commands, command =>
                command.Contains("ContractInvoices", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("ContractPayments", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("UNION ALL", linkedContractsCommand, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static CasesV2Controller CreateController(PaymentContext db, ClaimsPrincipal principal)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };

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
