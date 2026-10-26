using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, "case-only", false)]
    [InlineData(true, "case-only", false)]
    [InlineData(false, "denied-own", false)]
    [InlineData(true, "denied-own", false)]
    [InlineData(false, "allowed-own", true)]
    [InlineData(true, "allowed-own", true)]
    [InlineData(false, "allowed-department", true)]
    [InlineData(true, "allowed-department", true)]
    [InlineData(false, "denied-department", false)]
    [InlineData(true, "denied-department", false)]
    [InlineData(false, "allowed-all", true)]
    [InlineData(true, "allowed-all", true)]
    [InlineData(false, "admin", true)]
    [InlineData(true, "admin", true)]
    [InlineData(false, "revoked", false)]
    [InlineData(true, "revoked", false)]
    [InlineData(false, "unapproved", false)]
    [InlineData(true, "unapproved", false)]
    public async Task CaseCalendar_RequestTaskPrivacyMatchesRequestsScopeAcrossEventsSummaryIcsAndDirectTask(
        bool postgres, string mode, bool requestAllowed)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedCaseCalendarPrivacyAsync(db, mode);
        Assert.DoesNotContain(await db.RolePermissions.Where(permission => permission.RoleId == seed.Actor.RoleId).ToArrayAsync(),
            permission => permission.Section is RolePermissionCatalog.Tasks or RolePermissionCatalog.Calendar);
        var controller = CreateController(db, seed.Actor);

        // Omit includeTasks to characterize the public API's true default.
        var events = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null));
        Assert.Contains(events, item => item.SourceId == seed.OrdinaryTask.Id && item.Kind == "task");
        Assert.Contains(events, item => item.Kind == "case-deadline" && item.ClientCaseId == seed.Case.Id);
        Assert.Equal(requestAllowed, events.Any(item => item.Kind == "task" && item.SourceId == seed.RequestTask.Id));
        Assert.Equal(requestAllowed ? 2 : 1, events.Count(item => item.Kind == "task"));

        var summaryResult = await controller.GetCaseCalendarSummary(seed.From, seed.To, null, null);
        var summary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(Assert.IsType<OkObjectResult>(summaryResult.Result).Value);
        Assert.Equal(requestAllowed ? 2 : 1, summary.TaskCount);
        Assert.Equal(requestAllowed ? 3 : 2, summary.TotalEventCount);
        Assert.NotNull(summary.NearestEvent);
        Assert.Equal(requestAllowed ? seed.RequestTask.Id : seed.OrdinaryTask.Id, summary.NearestEvent.SourceId);
        Assert.Equal(requestAllowed ? seed.RequestTask.Title : seed.OrdinaryTask.Title, summary.NearestEvent.Title);

        var export = Assert.IsType<FileContentResult>(await controller.ExportCaseCalendarIcs(seed.From, seed.To, null, null));
        var ics = Encoding.UTF8.GetString(export.FileContents);
        Assert.Contains(seed.OrdinaryTask.Title, ics, StringComparison.Ordinal);
        Assert.Equal(requestAllowed, ics.Contains(seed.RequestTask.Title, StringComparison.Ordinal));
        if (!requestAllowed) Assert.DoesNotContain($"task:{seed.RequestTask.Id}", ics, StringComparison.Ordinal);

        var withoutTasks = CalendarPrivacyEvents(await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null, includeTasks: false));
        Assert.Single(withoutTasks);
        Assert.All(withoutTasks, item => Assert.Equal("case-deadline", item.Kind));
        var withoutSummary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetCaseCalendarSummary(seed.From, seed.To, null, null, includeTasks: false)).Result).Value);
        Assert.Equal(0, withoutSummary.TaskCount);
        Assert.Equal(1, withoutSummary.TotalEventCount);
        var withoutIcs = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(
            await controller.ExportCaseCalendarIcs(seed.From, seed.To, null, null, includeTasks: false)).FileContents);
        Assert.DoesNotContain(seed.RequestTask.Title, withoutIcs, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.OrdinaryTask.Title, withoutIcs, StringComparison.Ordinal);

        // Tasks API still applies its own module permission and the same request-only predicate.
        await GrantSectionPermissionAsync(db, seed.Actor, RolePermissionCatalog.Tasks, viewScope: DataVisibilityScope.All);
        var tasks = new TasksController(db, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateController(db, seed.Actor).User }
            }
        };
        Assert.IsType<OkObjectResult>((await tasks.GetTask(seed.OrdinaryTask.Id, CancellationToken.None)).Result);
        var direct = (await tasks.GetTask(seed.RequestTask.Id, CancellationToken.None)).Result;
        if (requestAllowed) Assert.IsType<OkObjectResult>(direct);
        else Assert.IsType<NotFoundResult>(direct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseCalendar_RequestPrivacyAppliesBeforeCase1000AndTask2000Limits(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedCaseCalendarPrivacyAsync(db, "case-only");
        var hiddenCases = Enumerable.Range(0, 1000).Select(index => new ClientCase
        {
            ClientId = seed.Case.ClientId,
            ResponsibleUserId = seed.Actor.Id,
            Title = $"Private request-only case {index}",
            Status = ClientCaseStatus.Open,
            StartDate = seed.From.AddDays(-1)
        }).ToArray();
        db.ClientCases.AddRange(hiddenCases);
        await db.SaveChangesAsync();
        var requests = hiddenCases.Select(item => new SalesRequest
        {
            ClientId = item.ClientId!.Value,
            ClientCaseId = item.Id,
            ResponsibleUserId = seed.Other.Id,
            CreatedByUserId = seed.Other.Id,
            StatusId = seed.Request.StatusId,
            Summary = "Private request-only case"
        }).ToArray();
        db.SalesRequests.AddRange(requests);
        await db.SaveChangesAsync();
        db.TaskJournalItems.AddRange(hiddenCases.Select((item, index) => CalendarPrivacyTask(seed.Other, item,
            "PRIVATE SALES CASE " + index, seed.From.AddHours(1), requests[index].Id)));
        db.TaskJournalItems.AddRange(Enumerable.Range(0, 2000).Select(index => CalendarPrivacyTask(seed.Other, seed.Case,
            "PRIVATE SALES LIMIT " + index, seed.From.AddHours(1), seed.Request.Id)));
        await db.SaveChangesAsync();

        var events = CalendarPrivacyEvents(await CreateController(db, seed.Actor).GetCaseCalendarEvents(seed.From, seed.To, null, null));

        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal(seed.Case.Id, item.ClientCaseId));
        Assert.Equal(seed.OrdinaryTask.Id, Assert.Single(events, item => item.Kind == "task").SourceId);
        Assert.Contains(events, item => item.Kind == "case-deadline");
        Assert.DoesNotContain(events, item => item.Title.StartsWith("PRIVATE SALES", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseCalendar_RequestPrivacyUsesFixedQueryCountAsOrdinaryTasksGrow(bool postgres)
    {
        var recorder = new RecordingCommandInterceptor();
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(lease!.ConnectionString).AddInterceptors(recorder).Options)
            : await SqliteTestContextFactory.CreateAsync(interceptor: recorder);
        var seed = await SeedCaseCalendarPrivacyAsync(db, "case-only");
        recorder.Commands.Clear();
        Assert.Equal(2, CalendarPrivacyEvents(await CreateController(db, seed.Actor)
            .GetCaseCalendarEvents(seed.From, seed.To, null, null)).Count);
        var before = recorder.Commands.Count;
        Assert.True(before > 0);
        db.TaskJournalItems.AddRange(Enumerable.Range(0, 25).Select(index => CalendarPrivacyTask(seed.Other, seed.Case,
            "Ordinary allowed task " + index, seed.From.AddDays(2))));
        await db.SaveChangesAsync();
        recorder.Commands.Clear();

        var events = CalendarPrivacyEvents(await CreateController(db, seed.Actor).GetCaseCalendarEvents(seed.From, seed.To, null, null));

        Assert.Equal(27, events.Count);
        Assert.Equal(before, recorder.Commands.Count);
        Assert.DoesNotContain(events, item => item.SourceId == seed.RequestTask.Id && item.Kind == "task");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseCalendar_InactiveActorCannotReadEventsSummaryOrIcs(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedCaseCalendarPrivacyAsync(db, "allowed-all");
        seed.Actor.IsActive = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, seed.Actor);
        Assert.IsType<ForbidResult>((await controller.GetCaseCalendarEvents(seed.From, seed.To, null, null)).Result);
        Assert.IsType<ForbidResult>((await controller.GetCaseCalendarSummary(seed.From, seed.To, null, null)).Result);
        Assert.IsType<ForbidResult>(await controller.ExportCaseCalendarIcs(seed.From, seed.To, null, null));
    }

    private static IReadOnlyList<CasesV2Controller.CaseCalendarEventDto> CalendarPrivacyEvents(
        ActionResult<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>> result)
        => Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private sealed record CalendarPrivacySeed(User Actor, User Other, ClientCase Case, SalesRequest Request,
        TaskJournalItem RequestTask, TaskJournalItem OrdinaryTask, DateTime From, DateTime To);

    private static async Task<CalendarPrivacySeed> SeedCaseCalendarPrivacyAsync(PaymentContext db, string mode)
    {
        var actor = await CreateUserAsync(db, $"calendar-actor-{Guid.NewGuid():N}@example.test", "Case-only lawyer");
        var other = await CreateUserAsync(db, $"calendar-sales-{Guid.NewGuid():N}@example.test", "Request manager");
        await GrantSectionPermissionAsync(db, other, RolePermissionCatalog.Requests, viewScope: DataVisibilityScope.All);
        if (mode != "case-only" && mode != "admin")
            await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Requests, canView: mode != "revoked",
                viewScope: mode is "allowed-department" or "denied-department" ? DataVisibilityScope.Department
                    : mode is "allowed-own" or "denied-own" ? DataVisibilityScope.Own : DataVisibilityScope.All);
        if (mode == "admin") actor.Role!.Name = "admin";
        if (mode == "unapproved") actor.IsApproved = false;
        actor.Department = "Calendar privacy department";
        other.Department = mode == "allowed-department" ? actor.Department : "Other department";
        var from = DateTime.UtcNow.Date.AddDays(2);
        var client = new Client { Name = "Calendar privacy client", IsActive = true, ResponsibleUserId = actor.Id };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Allowed case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = actor.Id,
            DueDate = from.AddDays(3)
        };
        var status = new SalesRequestStatus { Key = "calendar-privacy", Title = "Private sales stage" };
        db.AddRange(clientCase, status);
        db.UserSettings.Add(new UserSetting { UserId = actor.Id, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "UTC" });
        await db.SaveChangesAsync();
        var request = new SalesRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            StatusId = status.Id,
            ResponsibleUserId = other.Id,
            CreatedByUserId = mode == "allowed-own" ? actor.Id : other.Id,
            Summary = "Private sales request",
            Amount = 123456m,
            Comment = "Private sales negotiations"
        };
        db.SalesRequests.Add(request);
        await db.SaveChangesAsync();
        var requestTask = CalendarPrivacyTask(other, clientCase, "PRIVATE SALES TASK", from.AddHours(1), request.Id);
        var ordinaryTask = CalendarPrivacyTask(other, clientCase, "Ordinary case task", from.AddDays(2));
        db.TaskJournalItems.AddRange(requestTask, ordinaryTask);
        await db.SaveChangesAsync();
        return new(actor, other, clientCase, request, requestTask, ordinaryTask, from, from.AddDays(5));
    }

    private static TaskJournalItem CalendarPrivacyTask(User assignee, ClientCase clientCase, string title, DateTime dueAt, int? requestId = null)
        => new()
        {
            ClientCaseId = clientCase.Id,
            SalesRequestId = requestId,
            ObjectName = clientCase.Title,
            Title = title,
            DueAt = dueAt,
            PlannedDurationMinutes = 45,
            Type = "task",
            Priority = "normal",
            Status = "inProgress",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            AuthorUserId = assignee.Id,
            AuthorName = assignee.FullName
        };
}
