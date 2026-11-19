using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Fact]
    public async Task CaseCalendar_EmployeeSelection_FiltersEveryEventKindSummaryAndIcs()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "calendar-selection@example.test", "Viewer", DataVisibilityScope.All);
        var first = await CreateUserAsync(db, "calendar-first@example.test", "First");
        var second = await CreateUserAsync(db, "calendar-second@example.test", "Second");
        var from = DateTime.UtcNow.Date.AddDays(2);
        var cases = new[] { viewer, first, second }.Select(owner => new ClientCase
        {
            Client = new Client { Name = owner.FullName, ResponsibleUserId = owner.Id, IsActive = true },
            ResponsibleUserId = owner.Id,
            Title = owner.FullName + " case",
            DueDate = from.AddDays(1),
            CourtEvents = [new ClientCaseCourtEvent { Title = owner.FullName + " hearing", ScheduledAt = from.AddDays(2), AllDay = true }],
            ProceduralDeadlines = [new ClientCaseProceduralDeadline { Title = owner.FullName + " deadline", DeadlineType = "appeal", DueAt = from.AddDays(3) }],
        }).ToArray();
        db.ClientCases.AddRange(cases);
        await db.SaveChangesAsync();
        // The existing responsible filter selects the case owner, including its tasks assigned to another employee.
        db.TaskJournalItems.AddRange(cases.Select(item => CalendarPrivacyTask(viewer, item, item.Title + " task", from.AddDays(4))));
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);
        var selection = new[] { first.Id, second.Id, first.Id };
        var to = from.AddDays(5);

        var events = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, null, responsibleUserIds: selection));

        Assert.Equal(8, events.Count);
        Assert.Equal(new[] { cases[1].Id, cases[2].Id }, events.Select(item => item.ClientCaseId).Distinct().Order().ToArray());
        Assert.Equal(new[] { "case-deadline", "court", "procedural-deadline", "task" }, events.Select(item => item.Kind).Distinct().Order().ToArray());
        var summary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetCaseCalendarSummary(from, to, null, null, responsibleUserIds: selection)).Result).Value);
        Assert.Equal(events.Count, summary.TotalEventCount);
        Assert.Equal(2, summary.CourtEventCount);
        Assert.Equal(2, summary.ProceduralDeadlineCount);
        Assert.Equal(2, summary.TaskCount);
        var ics = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(
            await controller.ExportCaseCalendarIcs(from, to, null, null, responsibleUserIds: selection)).FileContents);
        Assert.Contains("First hearing", ics);
        Assert.Contains("Second deadline", ics);
        Assert.Contains("First case task", ics);
        Assert.DoesNotContain("Viewer case", ics);
        Assert.DoesNotContain("Viewer hearing", ics);

        var legacy = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, first.Id));
        var intersected = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, first.Id, responsibleUserIds: selection));
        Assert.Equal(legacy.Select(item => item.Id), intersected.Select(item => item.Id));
        Assert.Empty(CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, viewer.Id, responsibleUserIds: selection)));
        Assert.Empty(CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, cases[0].Id, null, responsibleUserIds: selection)));
        var noTasks = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, null, includeTasks: false, responsibleUserIds: selection));
        Assert.Equal(6, noTasks.Count);
        Assert.DoesNotContain(noTasks, item => item.Kind == "task");

        var all = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, null));
        var emptySelection = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(from, to, null, null, responsibleUserIds: []));
        Assert.Equal(all.Select(item => item.Id), emptySelection.Select(item => item.Id));
        Assert.Equal(12, emptySelection.Count);
    }

    [Fact]
    public async Task CaseCalendar_EmployeeSelection_CannotExpandCaseOrSalesRequestAccess()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedCaseCalendarPrivacyAsync(db, "case-only");
        var hiddenCase = new ClientCase
        {
            Client = new Client { Name = "Hidden client", ResponsibleUserId = seed.Other.Id, IsActive = true },
            ResponsibleUserId = seed.Other.Id,
            Title = "Hidden case",
            DueDate = seed.From.AddDays(1),
        };
        db.ClientCases.Add(hiddenCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, seed.Actor);
        var selected = new[] { seed.Actor.Id, seed.Other.Id };

        var events = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null, responsibleUserIds: selected));
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal(seed.Case.Id, item.ClientCaseId));
        Assert.DoesNotContain(events, item => item.Kind == "task" && item.SourceId == seed.RequestTask.Id);
        var summary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetCaseCalendarSummary(seed.From, seed.To, null, null, responsibleUserIds: selected)).Result).Value);
        Assert.Equal(events.Count, summary.TotalEventCount);
        var ics = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(
            await controller.ExportCaseCalendarIcs(seed.From, seed.To, null, null, responsibleUserIds: selected)).FileContents);
        Assert.DoesNotContain("Hidden case", ics);
        Assert.DoesNotContain(seed.RequestTask.Title, ics);

        await GrantSectionPermissionAsync(db, seed.Actor, RolePermissionCatalog.Cases, canView: false);
        controller = CreateController(db, seed.Actor);
        Assert.IsType<ForbidResult>((await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null, responsibleUserIds: selected)).Result);
        Assert.IsType<ForbidResult>((await controller.GetCaseCalendarSummary(seed.From, seed.To, null, null, responsibleUserIds: selected)).Result);
        Assert.IsType<ForbidResult>(await controller.ExportCaseCalendarIcs(seed.From, seed.To, null, null, responsibleUserIds: selected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task CaseCalendar_EmployeeSelection_RejectsInvalidAndOversizedListsBeforeExport(int invalid)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "calendar-boundary@example.test", "Viewer", DataVisibilityScope.All);
        var selected = invalid == 501 ? Enumerable.Repeat(actor.Id, 501).ToArray() : new[] { actor.Id, invalid };
        var controller = CreateController(db, actor);
        var auditBefore = JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());

        Assert.IsType<BadRequestObjectResult>((await controller.GetCaseCalendarEvents(null, null, null, null, responsibleUserIds: selected)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCaseCalendarSummary(null, null, null, null, responsibleUserIds: selected)).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.ExportCaseCalendarIcs(null, null, null, null, responsibleUserIds: selected));
        Assert.Equal(auditBefore, JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));
        Assert.IsType<OkObjectResult>((await controller.GetCaseCalendarEvents(null, null, null, null,
            responsibleUserIds: Enumerable.Repeat(actor.Id, 500).ToArray())).Result);
    }
}
