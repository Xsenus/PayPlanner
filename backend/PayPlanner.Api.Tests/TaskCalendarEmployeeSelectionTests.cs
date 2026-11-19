using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class TasksControllerTests
{
    [Fact]
    public async Task ExportCalendar_EmployeeSelection_IncludesPrimaryAndAdditionalExecutorsAndPreservesFilters()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-export-admin@example.test", "Admin");
        var selected = await CreateTaskUserAsync(db, "calendar-export-selected@example.test", "Selected", canEdit: false);
        var other = await CreateTaskUserAsync(db, "calendar-export-other@example.test", "Other", canEdit: false);
        var client = await CreateClientAsync(db, "Selected client");
        var anotherClient = await CreateClientAsync(db, "Another client");
        var primary = await CreateTaskAsync(db, other.Id, client.Id, assigneeUserId: selected.Id);
        primary.Title = "Selected primary executor";
        var additional = await CreateTaskAsync(db, other.Id, client.Id, assigneeUserId: other.Id);
        additional.Title = "Selected additional executor";
        additional.Participants.Add(new TaskJournalParticipant { UserId = selected.Id, Role = "assignee", DisplayName = selected.FullName });
        var observer = await CreateTaskAsync(db, selected.Id, client.Id, assigneeUserId: other.Id);
        observer.Title = "Only author and observer";
        observer.Participants.Add(new TaskJournalParticipant { UserId = selected.Id, Role = "observer", DisplayName = selected.FullName });
        var outsideClient = await CreateTaskAsync(db, other.Id, anotherClient.Id, assigneeUserId: selected.Id);
        outsideClient.Title = "Outside client filter";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var file = Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null, client.Id, null, null,
            CancellationToken.None, assigneeUserIds: [selected.Id, selected.Id]));
        var content = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains(primary.Title, content);
        Assert.Contains(additional.Title, content);
        Assert.DoesNotContain(observer.Title, content);
        Assert.DoesNotContain(outsideClient.Title, content);
        Assert.Equal(2, content.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);

        var legacy = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null,
            client.Id, null, null, CancellationToken.None)).FileContents);
        var empty = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null,
            client.Id, null, null, CancellationToken.None, assigneeUserIds: [])).FileContents);
        Assert.Contains(observer.Title, empty);
        Assert.Equal(legacy.Split("BEGIN:VEVENT", StringSplitOptions.None).Length, empty.Split("BEGIN:VEVENT", StringSplitOptions.None).Length);
    }

    [Fact]
    public async Task ExportCalendar_EmployeeSelection_PreservesVisibilityAndExportPermission()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateTaskUserAsync(db, "calendar-export-own@example.test", "Own", canEdit: false);
        var other = await CreateTaskUserAsync(db, "calendar-export-hidden@example.test", "Other", canEdit: false);
        var client = await CreateClientAsync(db, "Private client");
        var allowed = await CreateTaskAsync(db, actor.Id, client.Id, assigneeUserId: other.Id);
        allowed.Title = "Allowed authored task";
        var hidden = await CreateTaskAsync(db, other.Id, client.Id, assigneeUserId: other.Id);
        hidden.Title = "Hidden unrelated task";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), actor);

        var content = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null,
            null, null, null, null, CancellationToken.None, assigneeUserIds: [other.Id])).FileContents);
        Assert.Contains(allowed.Title, content);
        Assert.DoesNotContain(hidden.Title, content);
        var report = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html",
            null, null, null, null, null, null, assigneeUserIds: [other.Id])).FileContents);
        Assert.Contains(allowed.Title, report);
        Assert.DoesNotContain(hidden.Title, report);

        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Tasks);
        permission.CanExport = false;
        await db.SaveChangesAsync();
        controller = CreateController(db, new RecordingNotificationService(), actor);
        Assert.IsType<ForbidResult>(await controller.ExportCalendar(null, null, null, null, null, null,
            CancellationToken.None, assigneeUserIds: [other.Id]));
        Assert.IsType<ForbidResult>(await controller.ExportPlanningReport("html", null, null, null, null, null, null,
            assigneeUserIds: [other.Id]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task ExportCalendar_EmployeeSelection_ValidatesBoundedPositiveIds(int invalid)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-export-limit@example.test", "Admin");
        var controller = CreateController(db, new RecordingNotificationService(), actor);
        var ids = invalid == 501 ? Enumerable.Repeat(actor.Id, 501).ToArray() : new[] { actor.Id, invalid };
        var auditBefore = JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());

        Assert.IsType<BadRequestObjectResult>(await controller.ExportCalendar(null, null, null, null, null, null,
            CancellationToken.None, assigneeUserIds: ids));
        Assert.IsType<BadRequestObjectResult>(await controller.ExportPlanningReport("html", null, null, null, null, null, null,
            assigneeUserIds: ids));
        Assert.Equal(auditBefore, JsonSerializer.Serialize(await db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));
        Assert.IsType<FileContentResult>(await controller.ExportCalendar(null, null, null, null, null, null,
            CancellationToken.None, assigneeUserIds: Enumerable.Repeat(actor.Id, 500).ToArray()));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("word")]
    [InlineData("pdf")]
    public async Task ExportPlanningReport_EmployeeSelection_MatchesExecutorAndLegacyFiltersAcrossFormats(string format)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateAdminUserAsync(db, "calendar-report-admin@example.test", "Admin");
        var selected = await CreateTaskUserAsync(db, "calendar-report-selected@example.test", "Selected", canEdit: false);
        var other = await CreateTaskUserAsync(db, "calendar-report-other@example.test", "Other", canEdit: false);
        var client = await CreateClientAsync(db, "Report client");
        var primary = await CreateTaskAsync(db, actor.Id, client.Id, assigneeUserId: selected.Id);
        primary.Title = "Selected primary work";
        var additional = await CreateTaskAsync(db, actor.Id, client.Id, assigneeUserId: other.Id);
        additional.Title = "Selected additional work";
        additional.Participants.Add(new TaskJournalParticipant { UserId = selected.Id, Role = "assignee", DisplayName = selected.FullName });
        var excluded = await CreateTaskAsync(db, selected.Id, client.Id, assigneeUserId: other.Id);
        excluded.Title = "Excluded author only work";
        await db.SaveChangesAsync();
        var pdfService = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), actor, pdfService);

        var file = Assert.IsType<FileContentResult>(await controller.ExportPlanningReport(format, null, null, null, null,
            null, null, assigneeUserIds: [selected.Id]));
        var text = format == "pdf" ? pdfService.LastHtml : Encoding.UTF8.GetString(file.FileContents);
        if (format == "word")
        {
            using var archive = new ZipArchive(new MemoryStream(file.FileContents), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
            text = await reader.ReadToEndAsync();
        }

        Assert.Contains(primary.Title, text);
        Assert.Contains(additional.Title, text);
        Assert.DoesNotContain(excluded.Title, text);
        var intersection = Assert.IsType<FileContentResult>(await controller.ExportPlanningReport("html", null, null, null,
            other.Id, null, null, assigneeUserIds: [selected.Id]));
        var intersectionText = Encoding.UTF8.GetString(intersection.FileContents);
        Assert.Contains(additional.Title, intersectionText);
        Assert.DoesNotContain(primary.Title, intersectionText);
        Assert.DoesNotContain(excluded.Title, intersectionText);
    }
}
