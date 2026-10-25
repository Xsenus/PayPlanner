using System.Data.Common;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class TasksControllerTests
{
    [Theory]
    [InlineData("html", 0)]
    [InlineData("html", 1)]
    [InlineData("html", 51)]
    [InlineData("html", 5000)]
    [InlineData("word", 0)]
    [InlineData("word", 1)]
    [InlineData("word", 51)]
    [InlineData("word", 5000)]
    [InlineData("pdf", 0)]
    [InlineData("pdf", 1)]
    [InlineData("pdf", 51)]
    [InlineData("pdf", 5000)]
    public async Task ExportPlanningReport_PreservesAllRowsUpToExplicitLimit(string format, int count)
    {
        var queries = new PlanningLimitQueryRecorder();
        await using var db = await CreateContextAsync(queries, useObjectAuditModel: false);
        var user = await CreateAdminUserAsync(db, "planning-limit@fixture.local", "Автор планинга");
        await AddPlanningLimitTasksAsync(db, user, count);
        db.ChangeTracker.Clear();
        queries.Commands.Clear();
        var pdf = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), user, pdf);

        var result = await controller.ExportPlanningReport(format, null, "all", null, null, null, null);

        var file = Assert.IsType<FileContentResult>(result);
        var content = ReadPlanningLimitContent(file, format, pdf);
        Assert.Contains($"Задач: {count}.", content);
        var titles = Regex.Matches(content, @"Planning limit row \d{4}").Select(match => match.Value).ToArray();
        Assert.Equal(count, titles.Length);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"Planning limit row {index:D4}"), titles);
        if (count == 0) Assert.Contains("По выбранным фильтрам задач нет.", content);
        AssertPlanningQueryBounded(queries);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("word")]
    [InlineData("pdf")]
    public async Task ExportPlanningReport_Rejects5001RowsBeforeDocumentRendering(string format)
    {
        var queries = new PlanningLimitQueryRecorder();
        await using var db = await CreateContextAsync(queries, useObjectAuditModel: false);
        var user = await CreateAdminUserAsync(db, "planning-over-limit@fixture.local", "Автор планинга");
        await AddPlanningLimitTasksAsync(db, user, 5001);
        db.ChangeTracker.Clear();
        queries.Commands.Clear();
        var pdf = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), user, pdf);

        var result = await controller.ExportPlanningReport(format, null, "all", null, null, null, null);

        var error = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("В планинг можно выгрузить не более 5000 задач за один раз. Сузьте отбор по датам, клиенту или другим фильтрам и повторите выгрузку.", error.Value);
        Assert.Empty(pdf.LastHtml);
        AssertPlanningQueryBounded(queries);
    }

    [Fact]
    public async Task ExportPlanningReport_LimitCountsOnlyRowsMatchingEveryFilter()
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: false);
        var user = await CreateAdminUserAsync(db, "planning-filters@fixture.local", "Автор планинга");
        var other = await CreateAdminUserAsync(db, "planning-other-assignee@fixture.local", "Другой исполнитель");
        var client = await CreateClientAsync(db, "Клиент отфильтрованного планинга");
        var clientCase = await CreateCaseAsync(db, client.Id, "Дело отфильтрованного планинга");
        var legalEntity = await CreateLegalEntityAsync(db, "Компания отфильтрованного планинга");
        var dueAt = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var included = await AddPlanningLimitTasksAsync(db, user, 5000, task =>
        {
            task.ClientId = client.Id;
            task.ClientCaseId = clientCase.Id;
            task.LegalEntityId = legalEntity.Id;
            task.DueAt = dueAt;
            task.Type = "contact";
            task.Priority = "urgent";
            task.Status = "waiting";
        });
        Action<TaskJournalItem>[] excludeOneFilter =
        [
            task => task.Title = "Outside requested text",
            task => task.Status = "done",
            task => task.Type = "task",
            task => task.Priority = "normal",
            task => task.AssigneeUserId = other.Id,
            task => task.DueAt = dueAt.AddSeconds(-1),
            task => task.DueAt = dueAt.AddSeconds(1),
            task => task.ClientId = null,
            task => task.ClientCaseId = null,
            task => task.LegalEntityId = null,
        ];
        foreach (var change in excludeOneFilter)
        {
            await AddPlanningLimitTasksAsync(db, user, 1, task =>
            {
                task.ClientId = client.Id;
                task.ClientCaseId = clientCase.Id;
                task.LegalEntityId = legalEntity.Id;
                task.DueAt = dueAt;
                task.Type = "contact";
                task.Priority = "urgent";
                task.Status = "waiting";
                change(task);
            });
        }
        var controller = CreateController(db, new RecordingNotificationService(), user);

        var result = await controller.ExportPlanningReport("html", "Planning limit row", "active", "contact", user.Id,
            dueAt, dueAt, clientCase.Id, client.Id, legalEntity.Id, "urgent");

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Задач: 5000.", content);
        Assert.Equal(5000, Regex.Matches(content, @"Planning limit row \d{4}").Count);
        Assert.Contains(included[0].Title, content);
        Assert.Contains(included[^1].Title, content);
    }

    [Fact]
    public async Task ExportPlanningReport_HiddenRowsDoNotTriggerLimitOrRevealTheirContents()
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: false);
        var user = await CreateTaskUserAsync(db, "planning-own-limit@fixture.local", "Ограниченный планировщик", canEdit: false);
        var other = await CreateTaskUserAsync(db, "planning-hidden-limit@fixture.local", "Другой планировщик", canEdit: false);
        await AddPlanningLimitTasksAsync(db, user, 51);
        await AddPlanningLimitTasksAsync(db, other, 5001, task => task.Title = "PRIVATE outside visibility");
        // Even a task authored and assigned to the recipient stays hidden when its sales section is closed.
        await AddPlanningLimitTasksAsync(db, user, 1, task =>
        {
            task.Title = "PRIVATE sales task";
            task.SalesRequestId = 9001;
        });
        var controller = CreateController(db, new RecordingNotificationService(), user);

        var result = await controller.ExportPlanningReport("html", null, "all", null, null, null, null);

        var content = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents);
        Assert.Contains("Задач: 51.", content);
        Assert.Equal(51, Regex.Matches(content, @"Planning limit row \d{4}").Count);
        Assert.DoesNotContain("PRIVATE", content);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("word")]
    [InlineData("pdf")]
    public async Task ExportPlanningReport_ExportDenialPrecedesTaskReadAndRendering(string format)
    {
        var queries = new PlanningLimitQueryRecorder();
        await using var db = await CreateContextAsync(queries, useObjectAuditModel: false);
        var user = await CreateTaskUserAsync(db, "planning-no-export@fixture.local", "Нет экспорта", canEdit: false, canExport: false);
        await AddPlanningLimitTasksAsync(db, user, 1);
        db.ChangeTracker.Clear();
        queries.Commands.Clear();
        var pdf = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), user, pdf);

        var result = await controller.ExportPlanningReport(format, null, "all", null, null, null, null);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(pdf.LastHtml);
        Assert.DoesNotContain(queries.Commands, command => command.Sql.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal));
    }

    private static async Task<TaskJournalItem[]> AddPlanningLimitTasksAsync(
        PaymentContext db, User user, int count, Action<TaskJournalItem>? configure = null)
    {
        var dueAt = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var tasks = Enumerable.Range(0, count).Select(index => new TaskJournalItem
        {
            Title = $"Planning limit row {index:D4}",
            ObjectName = "Synthetic planning object",
            AuthorUserId = user.Id,
            AuthorName = user.FullName,
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = dueAt,
            Status = "waiting",
        }).ToArray();
        foreach (var task in tasks) configure?.Invoke(task);
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        return tasks;
    }

    private static string ReadPlanningLimitContent(FileContentResult file, string format, FakeInvoiceDocumentPdfService pdf)
    {
        if (format == "pdf")
        {
            Assert.Equal("application/pdf", file.ContentType);
            Assert.Equal([1, 2, 3], file.FileContents);
            return pdf.LastHtml;
        }
        if (format == "word")
        {
            using var archive = new ZipArchive(new MemoryStream(file.FileContents), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        Assert.Equal("text/html; charset=utf-8", file.ContentType);
        return Encoding.UTF8.GetString(file.FileContents);
    }

    private static void AssertPlanningQueryBounded(PlanningLimitQueryRecorder queries)
    {
        var taskQueries = queries.Commands.Where(command => command.Sql.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(taskQueries);
        Assert.All(taskQueries, command =>
        {
            Assert.Contains("LIMIT", command.Sql);
            Assert.Contains(command.Parameters, value => value is int limit && limit == 5001);
            Assert.DoesNotContain("COUNT(*)", command.Sql, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed class PlanningLimitQueryRecorder : DbCommandInterceptor
    {
        public List<(string Sql, object?[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
