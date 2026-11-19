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
    public async Task CaseCalendar_SelectedTimeZone_UsesEmployeeDayAcrossEventsSummaryAndIcs()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "calendar-zone-viewer@example.test", "Viewer", DataVisibilityScope.All);
        var employee = await CreateUserAsync(db, "calendar-zone-employee@example.test", "Employee");
        db.UserSettings.Add(new UserSetting
        {
            UserId = viewer.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Moscow",
        });
        var day = new DateTime(2026, 6, 4);
        var instant = new DateTime(2026, 6, 3, 14, 0, 0, DateTimeKind.Utc);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Calendar timezone client", ResponsibleUserId = employee.Id, IsActive = true },
            Title = "Calendar timezone case",
            ResponsibleUserId = employee.Id,
            DueDate = day,
            CourtEvents = [new ClientCaseCourtEvent { Title = "Employee morning hearing", ScheduledAt = instant, AllDay = false }],
            ProceduralDeadlines = [new ClientCaseProceduralDeadline { Title = "Employee morning deadline", DeadlineType = "appeal", DueAt = instant, AllDay = false }],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(CalendarPrivacyTask(employee, clientCase, "Employee morning task", instant));
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);
        var selection = new[] { employee.Id };

        var defaultEvents = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(day, day, null, null, responsibleUserIds: selection));
        Assert.Equal("case-deadline", Assert.Single(defaultEvents).Kind);
        var blankEvents = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(day, day, null, null,
            responsibleUserIds: selection, timeZoneId: " "));
        Assert.Equal(defaultEvents.Select(item => item.Id), blankEvents.Select(item => item.Id));
        var events = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(day, day, null, null,
            responsibleUserIds: selection, timeZoneId: "Asia/Kamchatka"));
        Assert.Equal(4, events.Count);
        Assert.Equal(instant, Assert.Single(events, item => item.Kind == "court").StartAt);
        Assert.Equal(day, Assert.Single(events, item => item.Kind == "case-deadline").StartAt);
        Assert.Contains(events, item => item.Kind == "procedural-deadline");
        Assert.Contains(events, item => item.Kind == "task");

        var summary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetCaseCalendarSummary(day, day, null, null, responsibleUserIds: selection, timeZoneId: "Asia/Kamchatka")).Result).Value);
        Assert.Equal(events.Count, summary.TotalEventCount);
        Assert.Equal(1, summary.CourtEventCount);
        Assert.Equal(1, summary.ProceduralDeadlineCount);
        Assert.Equal(1, summary.TaskCount);
        var actorSummary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetCaseCalendarSummary(day, day, null, null, responsibleUserIds: selection)).Result).Value);
        Assert.Equal(defaultEvents.Count, actorSummary.TotalEventCount);

        var file = Assert.IsType<FileContentResult>(await controller.ExportCaseCalendarIcs(day, day, null, null,
            responsibleUserIds: selection, timeZoneId: "Asia/Kamchatka"));
        var ics = Encoding.UTF8.GetString(file.FileContents);
        Assert.Equal(events.Count, ics.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        Assert.Contains("Employee morning hearing", ics);
        Assert.Contains("Employee morning deadline", ics);
        Assert.Contains("Employee morning task", ics);
        Assert.Contains("DTSTART:20260603T140000Z", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20260604", ics);
        var audit = await db.ObjectAuditEvents.AsNoTracking().SingleAsync(item => item.ObjectType == CalendarExportAudit.ObjectType);
        using var metadata = JsonDocument.Parse(audit.MetadataJson!);
        Assert.Equal(CalendarTimeZoneHelper.ResolveTimeZone("Asia/Kamchatka").Id,
            metadata.RootElement.GetProperty("filters").GetProperty("timeZoneId").GetString());
        var actorIcs = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCaseCalendarIcs(day, day, null,
            null, responsibleUserIds: selection)).FileContents);
        Assert.DoesNotContain("Employee morning hearing", actorIcs);
        Assert.DoesNotContain("Employee morning task", actorIcs);
    }

    [Theory]
    [InlineData("Unknown/TimeZone")]
    [InlineData("oversized")]
    public async Task CaseCalendar_SelectedTimeZone_RejectsInvalidZoneWithoutAuditWrites(string requestedZone)
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "calendar-zone-invalid@example.test", "Viewer", DataVisibilityScope.All);
        var controller = CreateController(db, viewer);
        var timeZoneId = requestedZone == "oversized" ? new string('x', 129) : requestedZone;
        var auditBefore = JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());

        Assert.IsType<BadRequestObjectResult>((await controller.GetCaseCalendarEvents(null, null, null, null, timeZoneId: timeZoneId)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCaseCalendarSummary(null, null, null, null, timeZoneId: timeZoneId)).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.ExportCaseCalendarIcs(null, null, null, null, timeZoneId: timeZoneId));
        Assert.Equal(auditBefore, JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));
    }

    [Fact]
    public async Task CaseCalendar_SelectedTimeZone_PreservesCaseAndRequestPermissions()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedCaseCalendarPrivacyAsync(db, "case-only");
        var controller = CreateController(db, seed.Actor);
        var selected = new[] { seed.Actor.Id, seed.Other.Id };

        var events = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null,
            responsibleUserIds: selected, timeZoneId: "Asia/Kamchatka"));
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, item => item.Kind == "task" && item.SourceId == seed.RequestTask.Id);
        await GrantSectionPermissionAsync(db, seed.Actor, RolePermissionCatalog.Cases, canView: false);
        controller = CreateController(db, seed.Actor);
        Assert.IsType<ForbidResult>((await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null,
            responsibleUserIds: selected, timeZoneId: "Asia/Kamchatka")).Result);
        Assert.IsType<ForbidResult>((await controller.GetCaseCalendarSummary(seed.From, seed.To, null, null,
            responsibleUserIds: selected, timeZoneId: "Asia/Kamchatka")).Result);
        Assert.IsType<ForbidResult>(await controller.ExportCaseCalendarIcs(seed.From, seed.To, null, null,
            responsibleUserIds: selected, timeZoneId: "Asia/Kamchatka"));
    }
}
