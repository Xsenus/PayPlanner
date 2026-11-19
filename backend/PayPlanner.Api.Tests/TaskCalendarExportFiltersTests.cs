using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class TasksControllerTests
{
    [Theory]
    [InlineData("HiddenLegal", 0)]
    [InlineData("1234567890123", 0)]
    [InlineData("Fallback", 1)]
    public async Task ExportCalendar_VisibleFilters_SearchesOnlyDisplayedCompanyName(string search, int expected)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-search-owner@example.test", "Owner");
        var shortNameCompany = await CreateLegalEntityAsync(db, "Visible company");
        shortNameCompany.FullName = "HiddenLegal corporate name";
        shortNameCompany.Inn = "1234567890123";
        var fallbackCompany = await CreateLegalEntityAsync(db, string.Empty);
        fallbackCompany.FullName = "Fallback legal company";
        var first = await CreateTaskAsync(db, actor.Id, null, legalEntityId: shortNameCompany.Id);
        first.Title = "Company task A";
        var second = await CreateTaskAsync(db, actor.Id, null, legalEntityId: fallbackCompany.Id);
        second.Title = "Company task B";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var calendarRows = Assert.IsType<PagedResponse<TaskJournalItemDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetTasks(search, "all", null, null, null, null, calendarView: true)).Result).Value);
        Assert.Equal(expected, calendarRows.Total);
        var legacyRows = Assert.IsType<PagedResponse<TaskJournalItemDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetTasks(search, "all", null, null, null, null)).Result).Value);
        Assert.Equal(1, legacyRows.Total);
        var ics = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null,
            null, null, null, CancellationToken.None, search: search, calendarView: true)).FileContents);
        Assert.Equal(expected, ics.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        var legacyIcs = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null,
            null, null, null, CancellationToken.None, search: search)).FileContents);
        Assert.Equal(1, legacyIcs.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        var report = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html", search,
            "all", null, null, null, null, calendarView: true)).FileContents);
        Assert.DoesNotContain(first.Title, report);
        Assert.Equal(expected == 1, report.Contains(second.Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportCalendar_VisibleFilters_MatchesInclusiveBoundsAndEveryRequestedFilter()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-filter-owner@example.test", "Owner");
        var employee = await CreateTaskUserAsync(db, "calendar-filter-assignee@example.test", "Employee", canEdit: false);
        var client = await CreateClientAsync(db, "Calendar client");
        var clientCase = await CreateCaseAsync(db, client.Id, "Calendar case");
        var from = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(1);
        var first = await CreateTaskAsync(db, actor.Id, client.Id, employee.Id, clientCase.Id, dueAt: from);
        var last = await CreateTaskAsync(db, actor.Id, client.Id, employee.Id, clientCase.Id, dueAt: to);
        foreach (var task in new[] { first, last })
        {
            task.Title = "Calendar match " + task.Id;
            task.Type = "contact";
            task.Priority = "urgent";
            task.IsUnderControl = true;
            task.Participants.Add(new TaskJournalParticipant { UserId = actor.Id, Role = "observer", DisplayName = actor.FullName });
        }

        Action<TaskJournalItem>[] excludeOneFilter =
        [
            task => task.Title = "Excluded text",
            task => task.Status = "done",
            task => task.Type = "task",
            task => task.Priority = "normal",
            task => task.IsUnderControl = false,
            task => task.AssigneeUserId = actor.Id,
            task => task.AuthorUserId = employee.Id,
            task => task.Participants.Clear(),
            task => task.DueAt = from.AddTicks(-1),
            task => task.DueAt = to.AddTicks(1),
            task => task.ClientId = null,
            task => task.ClientCaseId = null,
        ];
        foreach (var change in excludeOneFilter)
        {
            var task = await CreateTaskAsync(db, actor.Id, client.Id, employee.Id, clientCase.Id, dueAt: from);
            task.Title = "Calendar match excluded " + task.Id;
            task.Type = "contact";
            task.Priority = "urgent";
            task.IsUnderControl = true;
            task.Participants.Add(new TaskJournalParticipant { UserId = actor.Id, Role = "observer", DisplayName = actor.FullName });
            change(task);
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var file = Assert.IsType<FileContentResult>(await controller.ExportCalendar(from, to, clientCase.Id, client.Id,
            null, null, CancellationToken.None, assigneeUserIds: [employee.Id], search: "Calendar match", status: "active",
            type: "contact", assigneeUserId: employee.Id, priority: "urgent", underControl: true,
            authorUserId: actor.Id, participantRole: "observer", calendarView: true));
        var content = Encoding.UTF8.GetString(file.FileContents);
        Assert.Equal(2, content.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        Assert.Contains(first.Title, content);
        Assert.Contains(last.Title, content);
        Assert.DoesNotContain("excluded", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportCalendar_VisibleFilters_AppliesTodayBillingAndUntriagedFilters()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-metric-owner@example.test", "Owner");
        var client = await CreateClientAsync(db, "Calendar metric client");
        var today = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        var billable = await CreateTaskAsync(db, actor.Id, client.Id, dueAt: today.AddHours(14));
        billable.Title = "Billable work today";
        billable.ClientBillingClassification = TaskClientBillingClassification.Billable;
        var internalTask = await CreateTaskAsync(db, actor.Id, client.Id, dueAt: today.AddHours(14));
        internalTask.Title = "Internal work today";
        internalTask.ClientBillingClassification = TaskClientBillingClassification.Internal;
        var tomorrow = await CreateTaskAsync(db, actor.Id, client.Id, dueAt: today.AddDays(1));
        tomorrow.Title = "Billable work tomorrow";
        tomorrow.ClientBillingClassification = TaskClientBillingClassification.Billable;
        var untriaged = await CreateTaskAsync(db, actor.Id, null, dueAt: today);
        untriaged.Title = "Untriaged work";
        untriaged.AssigneeName = string.Empty;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var content = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null,
            null, null, null, null, CancellationToken.None, metric: "today", todayFrom: today, todayTo: today.AddDays(1),
            billingStatus: "unbilled", calendarView: true)).FileContents);
        Assert.Contains(billable.Title, content);
        Assert.DoesNotContain(internalTask.Title, content);
        Assert.DoesNotContain(tomorrow.Title, content);
        Assert.DoesNotContain(untriaged.Title, content);
        var onlyUntriaged = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null,
            null, null, null, null, CancellationToken.None, untriaged: true, calendarView: true)).FileContents);
        Assert.Contains(untriaged.Title, onlyUntriaged);
        Assert.DoesNotContain(billable.Title, onlyUntriaged);
    }

    [Theory]
    [InlineData(DataVisibilityScope.All, DataVisibilityScope.Own, 2)]
    [InlineData(DataVisibilityScope.Own, DataVisibilityScope.All, 3)]
    public async Task ExportCalendar_VisibleFilters_IntersectsTaskAndCalendarScopes(string taskScope, string calendarScope, int expected)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateTaskUserAsync(db, "calendar-scope-owner@example.test", "Owner", canEdit: false, viewScope: taskScope);
        var other = await CreateTaskUserAsync(db, "calendar-scope-other@example.test", "Other", canEdit: false);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            CanExport = false,
            ViewScope = calendarScope,
        });
        var own = await CreateTaskAsync(db, actor.Id, null, actor.Id);
        own.Title = "Own assigned work";
        var authored = await CreateTaskAsync(db, actor.Id, null, other.Id);
        authored.Title = "Only authored work";
        var assigned = await CreateTaskAsync(db, other.Id, null, actor.Id);
        assigned.Title = "Other authored assigned work";
        var hidden = await CreateTaskAsync(db, other.Id, null, other.Id);
        hidden.Title = "Other unrelated work";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var content = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null,
            null, null, null, null, CancellationToken.None, calendarView: true)).FileContents);
        Assert.Equal(expected, content.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        Assert.Contains(own.Title, content);
        Assert.Contains(assigned.Title, content);
        Assert.Equal(expected == 3, content.Contains(authored.Title, StringComparison.Ordinal));
        Assert.DoesNotContain(hidden.Title, content);
        var report = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html",
            null, "all", null, null, null, null, calendarView: true)).FileContents);
        Assert.Contains(own.Title, report);
        Assert.Contains(assigned.Title, report);
        Assert.Equal(expected == 3, report.Contains(authored.Title, StringComparison.Ordinal));
        Assert.DoesNotContain(hidden.Title, report);

        var legacy = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null,
            null, null, null, null, CancellationToken.None)).FileContents);
        Assert.Equal(taskScope == DataVisibilityScope.All ? 4 : 3, legacy.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task ExportCalendar_VisibleFilters_RejectsMissingCalendarViewAndRevokedTaskExport()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateTaskUserAsync(db, "calendar-scope-denied@example.test", "Owner", canEdit: false);
        var controller = CreateController(db, new RecordingNotificationService(), actor);
        var auditBefore = JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());
        Assert.IsType<ForbidResult>(await controller.ExportCalendar(null, null, null, null, null, null,
            CancellationToken.None, calendarView: true));
        Assert.IsType<ForbidResult>(await controller.ExportPlanningReport("html", null, null, null, null, null, null,
            calendarView: true));
        Assert.Equal(auditBefore, JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            ViewScope = DataVisibilityScope.All,
        });
        (await db.RolePermissions.SingleAsync(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Tasks)).CanExport = false;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await controller.ExportCalendar(null, null, null, null, null, null,
            CancellationToken.None, calendarView: true));
        Assert.IsType<ForbidResult>(await controller.ExportPlanningReport("html", null, null, null, null, null, null,
            calendarView: true));
    }

    [Theory]
    [InlineData("hide", false)]
    [InlineData("color", true)]
    public async Task ExportPlanningReport_VisibleFilters_AppliesConfiguredClosedPolicyOnlyForCalendar(string policy, bool includeClosed)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-closed-owner@example.test", "Owner");
        var active = await CreateTaskAsync(db, actor.Id, null);
        active.Title = "Active calendar work";
        var done = await CreateTaskAsync(db, actor.Id, null, status: "done");
        done.Title = "Completed calendar work";
        var cancelled = await CreateTaskAsync(db, actor.Id, null);
        cancelled.Status = " CANCELLED ";
        cancelled.Title = "Cancelled calendar work";
        db.SystemSettings.Add(new SystemSetting { Key = "calendarSync.settings", Value = JsonSerializer.Serialize(new { completedTaskCalendarAction = policy }) });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var report = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html", null,
            "all", null, null, null, null, calendarView: true)).FileContents);
        Assert.Contains(active.Title, report);
        Assert.Equal(includeClosed, report.Contains(done.Title, StringComparison.Ordinal));
        Assert.Equal(includeClosed, report.Contains(cancelled.Title, StringComparison.Ordinal));
        var legacy = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html", null,
            "all", null, null, null, null)).FileContents);
        Assert.Contains(done.Title, legacy);
        Assert.Contains(cancelled.Title, legacy);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("word")]
    [InlineData("pdf")]
    public async Task ExportPlanningReport_VisibleFilters_UsesSelectedTimeZoneAcrossFormats(string format)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-report-zone@example.test", "Owner");
        db.UserSettings.Add(new UserSetting { UserId = actor.Id, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "Europe/Moscow" });
        var task = await CreateTaskAsync(db, actor.Id, null, dueAt: new DateTime(2026, 6, 3, 14, 0, 0, DateTimeKind.Utc));
        task.Title = "Selected employee morning";
        await db.SaveChangesAsync();
        var pdf = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), actor, pdf);
        var file = Assert.IsType<FileContentResult>(await controller.ExportPlanningReport(format, null, "all", null, null,
            new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 4, 11, 59, 59, 999, DateTimeKind.Utc),
            calendarView: true, timeZoneId: "Asia/Kamchatka"));
        var content = format == "pdf" ? pdf.LastHtml : Encoding.UTF8.GetString(file.FileContents);
        if (format == "word")
        {
            using var archive = new ZipArchive(new MemoryStream(file.FileContents), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
            content = XDocument.Parse(await reader.ReadToEndAsync()).Root!.Value;
        }
        Assert.Contains(task.Title, content);
        Assert.Contains("04.06.2026 02:00", content);
        var legacy = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html", null,
            "all", null, null, null, null)).FileContents);
        Assert.Contains("03.06.2026 17:00", legacy);
    }

    [Theory]
    [InlineData("Unknown/TimeZone")]
    [InlineData("oversized")]
    public async Task ExportPlanningReport_VisibleFilters_RejectsInvalidZoneBeforeRenderingAndAudit(string requestedZone)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-export-bad-zone@example.test", "Owner");
        var pdf = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), actor, pdf);
        var auditBefore = JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());
        var zone = requestedZone == "oversized" ? new string('x', 129) : requestedZone;

        Assert.IsType<BadRequestObjectResult>(await controller.ExportPlanningReport("pdf", null, null, null, null, null, null,
            calendarView: true, timeZoneId: zone));
        Assert.Empty(pdf.LastHtml);
        Assert.Equal(auditBefore, JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task ExportCalendar_VisibleFilters_PreservesCompleteLimitOrRejectsBeforeAudit(int count)
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: false);
        var actor = await CreateAdminUserAsync(db, "calendar-export-cap@example.test", "Owner");
        await AddPlanningLimitTasksAsync(db, actor, count);
        var controller = CreateController(db, new RecordingNotificationService(), actor);
        var auditBefore = JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());

        var result = await controller.ExportCalendar(null, null, null, null, null, null, CancellationToken.None, calendarView: true);
        if (count == 1000)
        {
            var content = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents);
            Assert.Equal(count, content.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        }
        else
        {
            Assert.Contains("1000", Assert.IsType<BadRequestObjectResult>(result).Value!.ToString());
            Assert.Equal(auditBefore, JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));
            var filtered = Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null, null, null, null,
                CancellationToken.None, search: "no rows match this search", calendarView: true));
            Assert.DoesNotContain("BEGIN:VEVENT", Encoding.UTF8.GetString(filtered.FileContents));
        }
    }
}
