using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentSummaryQueryTests
{
    [Fact]
    public async Task Get_UsesSummaryTotalWithoutSeparatePaymentCountQuery()
    {
        var recorder = new RecordingCommandInterceptor();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .Options;
        await using var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Payments.AddRange(
            CreatePayment(1, PaymentType.Income, 12_000m),
            CreatePayment(2, PaymentType.Expense, 4_500m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        recorder.Commands.Clear();

        var controller = new PaymentsV2Controller(db);
        var result = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            excludeInstallmentParents: false,
            sortBy: "date",
            sortDir: "desc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);
        Assert.Equal(2, response.Total);
        Assert.Equal(response.Summary.Total, response.Total);
        Assert.Equal(12_000m, response.Summary.Income);
        Assert.Equal(4_500m, response.Summary.Expense);
        Assert.Equal(7_500m, response.Summary.Balance);

        var paymentCommands = recorder.Commands
            .Where(sql => sql.Contains("FROM \"Payments\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, paymentCommands.Length);
        Assert.DoesNotContain(
            recorder.Commands,
            sql => sql.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal)
                && !sql.Contains("FROM \"Payments\"", StringComparison.Ordinal));
    }

    private static Payment CreatePayment(int day, PaymentType type, decimal amount)
    {
        var date = new DateTime(2026, 7, day);
        return new Payment
        {
            Date = date,
            PlannedDate = date,
            Amount = amount,
            PaidAmount = 0m,
            Type = type,
            Status = PaymentStatus.Pending,
            Description = $"Платёж {day}",
            Notes = string.Empty,
            SystemNotes = string.Empty,
            CreatedAt = DateTime.UtcNow,
        };
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
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
