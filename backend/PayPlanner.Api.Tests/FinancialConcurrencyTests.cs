using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class FinancialConcurrencyTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_fin_conc_";

    [Fact]
    public async Task ConcurrentBalanceApplicationsNeverDebitClientOrLegalEntityTwice()
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
            var baseOptions = CreateOptions(testBuilder.ConnectionString);
            await using (var migrationDb = new PostgreSqlPaymentContext(baseOptions))
            {
                await migrationDb.Database.MigrateAsync();
            }

            await VerifyClientBalanceAsync(testBuilder.ConnectionString);
            await VerifyLegalEntityBalanceAsync(testBuilder.ConnectionString);
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task VerifyClientBalanceAsync(string connectionString)
    {
        int clientId;
        int paymentId;
        await using (var seedDb = new PostgreSqlPaymentContext(CreateOptions(connectionString)))
        {
            var client = new Client { Name = "Конкурентный клиент", CreatedAt = DateTime.UtcNow };
            var payment = CreatePayment(1_000m);
            payment.Client = client;
            seedDb.AddRange(
                client,
                payment,
                new ClientBalanceTransaction
                {
                    Client = client,
                    Amount = 1_000m,
                    Type = ClientBalanceTransactionType.Overpayment,
                    Direction = ClientBalanceDirection.Credit,
                    Description = "Переплата для конкурентного зачета",
                    CreatedAt = DateTime.UtcNow,
                });
            await seedDb.SaveChangesAsync();
            clientId = client.Id;
            paymentId = payment.Id;
        }

        var results = await ApplyClientCreditConcurrentlyAsync(connectionString, clientId, paymentId);
        Assert.Single(results, result => result is OkObjectResult);
        Assert.Single(results, result => result is ConflictObjectResult);

        await using var verifyDb = new PostgreSqlPaymentContext(CreateOptions(connectionString));
        var paymentAfter = await verifyDb.Payments.AsNoTracking().SingleAsync(item => item.Id == paymentId);
        Assert.Equal(1_000m, paymentAfter.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, paymentAfter.Status);
        Assert.Equal(1_000m, await ClientBalanceService.GetAppliedCreditAmountAsync(verifyDb, paymentId));
        Assert.Equal(0m, await ClientBalanceService.GetBalanceAsync(verifyDb, clientId));
    }

    private static async Task VerifyLegalEntityBalanceAsync(string connectionString)
    {
        int legalEntityId;
        int paymentId;
        await using (var seedDb = new PostgreSqlPaymentContext(CreateOptions(connectionString)))
        {
            var legalEntity = new LegalEntity { ShortName = "ООО Конкурентный баланс" };
            var payment = CreatePayment(1_500m);
            payment.LegalEntity = legalEntity;
            seedDb.AddRange(
                legalEntity,
                payment,
                new ClientBalanceTransaction
                {
                    LegalEntity = legalEntity,
                    Amount = 1_500m,
                    Type = ClientBalanceTransactionType.Overpayment,
                    Direction = ClientBalanceDirection.Credit,
                    Description = "Переплата юрлица для конкурентного зачета",
                    CreatedAt = DateTime.UtcNow,
                });
            await seedDb.SaveChangesAsync();
            legalEntityId = legalEntity.Id;
            paymentId = payment.Id;
        }

        var results = await ApplyLegalEntityCreditConcurrentlyAsync(connectionString, legalEntityId, paymentId);
        Assert.Single(results, result => result is OkObjectResult);
        Assert.Single(results, result => result is ConflictObjectResult);

        await using var verifyDb = new PostgreSqlPaymentContext(CreateOptions(connectionString));
        var paymentAfter = await verifyDb.Payments.AsNoTracking().SingleAsync(item => item.Id == paymentId);
        Assert.Equal(1_500m, paymentAfter.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, paymentAfter.Status);
        Assert.Equal(1_500m, await ClientBalanceService.GetAppliedCreditAmountAsync(verifyDb, paymentId));
        Assert.Equal(0m, await ClientBalanceService.GetLegalEntityBalanceAsync(verifyDb, legalEntityId));
    }

    private static async Task<IActionResult[]> ApplyClientCreditConcurrentlyAsync(
        string connectionString,
        int clientId,
        int paymentId)
    {
        var barrier = new BalanceReadBarrier();
        await using var firstDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        await using var secondDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        var first = new ClientBalancesController(firstDb);
        var second = new ClientBalancesController(secondDb);
        return await Task.WhenAll(
            first.ApplyCredit(clientId, new ApplyClientBalanceRequest { PaymentId = paymentId, Amount = 1_000m }, CancellationToken.None),
            second.ApplyCredit(clientId, new ApplyClientBalanceRequest { PaymentId = paymentId, Amount = 1_000m }, CancellationToken.None));
    }

    private static async Task<IActionResult[]> ApplyLegalEntityCreditConcurrentlyAsync(
        string connectionString,
        int legalEntityId,
        int paymentId)
    {
        var barrier = new BalanceReadBarrier();
        await using var firstDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        await using var secondDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        var first = new LegalEntityBalancesController(firstDb);
        var second = new LegalEntityBalancesController(secondDb);
        return await Task.WhenAll(
            first.ApplyCredit(legalEntityId, new ApplyClientBalanceRequest { PaymentId = paymentId, Amount = 1_500m }, CancellationToken.None),
            second.ApplyCredit(legalEntityId, new ApplyClientBalanceRequest { PaymentId = paymentId, Amount = 1_500m }, CancellationToken.None));
    }

    private static DbContextOptions<PostgreSqlPaymentContext> CreateOptions(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static Payment CreatePayment(decimal amount)
        => new()
        {
            Amount = amount,
            CounterpartyName = "Контрагент",
            Date = new DateTime(2026, 8, 23),
            PlannedDate = new DateTime(2026, 8, 23),
            Description = "Платеж для проверки конкурентного зачета",
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };

    private sealed class BalanceReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ClientBalanceTransactions", StringComparison.Ordinal)
                && command.CommandText.Contains("SUM", StringComparison.OrdinalIgnoreCase))
            {
                var arrival = Interlocked.Increment(ref _arrivals);
                if (arrival == 2)
                {
                    _release.TrySetResult();
                }

                if (arrival <= 2)
                {
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
