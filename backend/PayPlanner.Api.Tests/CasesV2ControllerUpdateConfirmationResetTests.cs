using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public Task CaseUpdate_ConcurrentConfirmationCannotRemainOnChangedSource(bool postgres, bool legacy, bool hearing)
        => RunCaseUpdateConfirmationRaceAsync(postgres, legacy, hearing, changeSource: true);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public Task CaseUpdate_UnchangedSourceKeepsConcurrentConfirmation(bool postgres, bool legacy, bool hearing)
        => RunCaseUpdateConfirmationRaceAsync(postgres, legacy, hearing, changeSource: false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseUpdate_ChangingOneSourcePreservesTheOtherConfirmation(bool legacy, bool hearing)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        var controller = CreateController(db, user);
        Assert.IsType<OkObjectResult>((await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt), CancellationToken.None)).Result);
        var before = await db.ClientCases.AsNoTracking().SingleAsync();
        var model = new ClientCase
        {
            ClientId = before.ClientId,
            Title = before.Title,
            Status = before.Status,
            ResponsibleUserId = user.Id,
            Strategy = hearing ? before.Strategy : "Новая позиция",
            NextHearingAt = hearing ? before.NextHearingAt!.Value.AddDays(1) : before.NextHearingAt,
            EventHistory = before.EventHistory,
        };
        var result = legacy
            ? await new CasesController(db) { ControllerContext = controller.ControllerContext }.Update(clientCase.Id, model, CancellationToken.None)
            : await controller.Update(clientCase.Id, model, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        if (hearing)
        {
            Assert.Null(actual.NextHearingConfirmedAt);
            Assert.Null(actual.NextHearingConfirmedForAt);
            Assert.Null(actual.NextHearingConfirmedByUserId);
            Assert.Empty(actual.NextHearingConfirmedByName);
            Assert.Equal(before.PositionReviewedAt, actual.PositionReviewedAt);
            Assert.Equal(before.PositionReviewedByUserId, actual.PositionReviewedByUserId);
            Assert.Equal(before.PositionReviewedByName, actual.PositionReviewedByName);
        }
        else
        {
            Assert.Null(actual.PositionReviewedAt);
            Assert.Null(actual.PositionReviewedByUserId);
            Assert.Empty(actual.PositionReviewedByName);
            Assert.Equal(before.NextHearingConfirmedAt, actual.NextHearingConfirmedAt);
            Assert.Equal(before.NextHearingConfirmedForAt, actual.NextHearingConfirmedForAt);
            Assert.Equal(before.NextHearingConfirmedByUserId, actual.NextHearingConfirmedByUserId);
            Assert.Equal(before.NextHearingConfirmedByName, actual.NextHearingConfirmedByName);
        }
        Assert.Equal(before.EventHistory, actual.EventHistory);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    private async Task RunCaseUpdateConfirmationRaceAsync(bool postgres, bool legacy, bool hearing, bool changeSource)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var sqlite = $"Data Source=case-update-confirmation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = postgres ? null : new SqliteConnection(sqlite);
        if (anchor is not null) await anchor.OpenAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(database!.ConnectionString).AddInterceptors(interceptors).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(sqlite).AddInterceptors(interceptors).Options, useObjectAuditModel: true);
        await using var seed = Open();
        if (!postgres) await seed.Database.EnsureCreatedAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(seed);
        var completed = new TaskJournalItem
        {
            Title = "Сохранённый результат",
            Status = "done",
            Result = "Исходный результат",
            ClientCaseId = clientCase.Id,
            AssigneeUserId = user.Id,
        };
        seed.TaskJournalItems.Add(completed);
        await seed.SaveChangesAsync();
        var confirmed = false;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open();
            var controller = CreateController(other, user);
            ActionResult result = hearing
                ? (await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt), ct)).Result!
                : (await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), ct)).Result!;
            Assert.True(result is OkObjectResult or ConflictObjectResult, result.GetType().Name);
            confirmed = result is OkObjectResult;
        });
        await using var request = Open(hook);
        var v2 = CreateController(request, user);
        var model = new ClientCase
        {
            ClientId = clientCase.ClientId,
            Title = clientCase.Title + " обновлено",
            Status = clientCase.Status,
            ResponsibleUserId = user.Id,
            Strategy = !changeSource || hearing ? clientCase.Strategy : "Изменённая позиция",
            NextHearingAt = changeSource && hearing ? clientCase.NextHearingAt!.Value.AddDays(1) : clientCase.NextHearingAt,
            EventHistory = clientCase.EventHistory,
        };
        var result = legacy
            ? await new CasesController(request) { ControllerContext = v2.ControllerContext }.Update(clientCase.Id, model, CancellationToken.None)
            : await v2.Update(clientCase.Id, model, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.True(hook.Called);
        await using var verify = Open();
        var actual = await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == clientCase.Id);
        Console.WriteLine($"CASE_UPDATE_CONFIRMATION postgres={postgres} legacy={legacy} hearing={hearing} changeSource={changeSource} confirmedBeforeWrite={confirmed}");
        Assert.Equal(model.Strategy, actual.Strategy);
        Assert.Equal(model.NextHearingAt, actual.NextHearingAt);
        if (changeSource || !confirmed)
        {
            Assert.Null(actual.PositionReviewedAt);
            Assert.Null(actual.PositionReviewedByUserId);
            Assert.Empty(actual.PositionReviewedByName);
            Assert.Null(actual.NextHearingConfirmedAt);
            Assert.Null(actual.NextHearingConfirmedForAt);
            Assert.Null(actual.NextHearingConfirmedByUserId);
            Assert.Empty(actual.NextHearingConfirmedByName);
        }
        else if (hearing)
        {
            Assert.NotNull(actual.NextHearingConfirmedAt);
            Assert.Equal(clientCase.NextHearingAt, actual.NextHearingConfirmedForAt);
            Assert.Equal(user.Id, actual.NextHearingConfirmedByUserId);
            Assert.Equal(user.FullName, actual.NextHearingConfirmedByName);
        }
        else
        {
            Assert.NotNull(actual.PositionReviewedAt);
            Assert.Equal(user.Id, actual.PositionReviewedByUserId);
            Assert.Equal(user.FullName, actual.PositionReviewedByName);
        }
        if (confirmed)
            Assert.Contains(hearing ? "Дата следующего заседания проверена" : "Позиция по делу проверена", actual.EventHistory);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        var savedTask = await verify.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Id == completed.Id);
        Assert.Equal("done", savedTask.Status);
        Assert.Equal("Исходный результат", savedTask.Result);
        Assert.Equal(clientCase.Id, savedTask.ClientCaseId);
        Assert.Empty(await verify.Payments.ToArrayAsync());
    }
}
