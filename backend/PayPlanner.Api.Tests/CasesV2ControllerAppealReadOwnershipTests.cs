using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("legacy")]
    [InlineData("court-moved")]
    [InlineData("deadline-moved")]
    [InlineData("court-cleared")]
    [InlineData("deadline-cleared")]
    [InlineData("only-court")]
    [InlineData("only-deadline")]
    public async Task GetCurrentCaseAppealDecision_BindsOptionalExpectedDatesWithoutWrites(string scenario)
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var lawyer = await CreateUserAsync(db, "appeal-bound@example.test", "Юрист");
        var court = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var deadline = court.AddDays(30);
        var item = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Акт для проверки",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = scenario == "court-cleared" ? null : court,
            AppealDeadlineAt = scenario == "deadline-cleared" ? null : deadline,
        };
        db.ClientCases.Add(item);
        await db.SaveChangesAsync();
        db.ClientCaseAppealDecisions.Add(new ClientCaseAppealDecision
        {
            ClientCaseId = item.Id,
            CourtDecisionDate = court,
            AppealDeadlineDate = deadline,
            Choice = "appeal",
            DecidedByUserName = "Исходный юрист",
        });
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var expectedCourt = scenario is "legacy" or "only-deadline" ? (DateTime?)null : court.AddDays(scenario == "court-moved" ? -1 : 0).AddHours(7);
        var expectedDeadline = scenario is "legacy" or "only-court" ? (DateTime?)null : deadline.AddDays(scenario == "deadline-moved" ? 1 : 0).AddHours(9);
        var result = await CreateController(db, lawyer).GetCurrentCaseAppealDecision(item.Id, CancellationToken.None, expectedCourt, expectedDeadline);
        if (scenario is "current" or "legacy")
        {
            var decision = Assert.IsType<CasesV2Controller.CaseAppealDecisionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(item.Id, decision.CaseId);
            Assert.Equal("Исходный юрист", decision.DecidedByUserName);
        }
        else
        {
            if (scenario is "only-court" or "only-deadline") Assert.IsType<BadRequestObjectResult>(result.Result);
            else Assert.IsType<ConflictObjectResult>(result.Result);
            Assert.DoesNotContain(interceptor.Commands, command => command.Contains("ClientCaseAppealDecisions", StringComparison.Ordinal));
        }
        Assert.Single(await db.ClientCaseAppealDecisions.ToListAsync());
        Assert.Empty(await db.TaskJournalItems.ToListAsync());
        Assert.Equal("Unchanged", db.Entry(item).State.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCurrentCaseAppealDecision_ChecksVisibilityBeforeDateConflicts(bool denyView)
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "appeal-owner@example.test", "Ответственный", DataVisibilityScope.Own);
        var outsider = await CreateUserAsync(db, "appeal-outsider@example.test", "Другой", DataVisibilityScope.Own);
        if (denyView) await GrantSectionPermissionAsync(db, outsider, RolePermissionCatalog.Cases, canView: false);
        var item = new ClientCase { Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = owner.Id }, Title = "Закрытое дело", Status = ClientCaseStatus.Open, ResponsibleUserId = owner.Id, CourtDecisionAt = DateTime.UtcNow.Date, AppealDeadlineAt = DateTime.UtcNow.Date.AddDays(30) };
        db.ClientCases.Add(item); await db.SaveChangesAsync();
        var result = await CreateController(db, outsider).GetCurrentCaseAppealDecision(item.Id, CancellationToken.None, DateTime.UtcNow.Date.AddDays(-10), null);
        if (denyView) Assert.IsType<ForbidResult>(result.Result);
        else Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetCurrentCaseAppealDecision_RejectsDateMismatchEvenWithoutRecordedChoice()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-empty@example.test", "Юрист");
        var court = DateTime.UtcNow.Date;
        var item = new ClientCase { Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id }, Title = "Нет решения", Status = ClientCaseStatus.Open, ResponsibleUserId = lawyer.Id, CourtDecisionAt = court, AppealDeadlineAt = court.AddDays(30) };
        db.ClientCases.Add(item); await db.SaveChangesAsync();
        var controller = CreateController(db, lawyer);
        Assert.IsType<ConflictObjectResult>((await controller.GetCurrentCaseAppealDecision(item.Id, CancellationToken.None, court.AddDays(-1), item.AppealDeadlineAt)).Result);
        Assert.Null(Assert.IsType<OkObjectResult>((await controller.GetCurrentCaseAppealDecision(item.Id, CancellationToken.None, court, item.AppealDeadlineAt)).Result).Value);
        Assert.Empty(await db.TaskJournalItems.ToListAsync());
    }
}
