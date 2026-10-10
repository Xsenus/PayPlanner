using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SalesRequestTests
{
    private sealed record Fixture(PaymentContext Db, SalesRequestService Service, User Manager, User Admin, Client Client);

    private static async Task<Fixture> CreateAsync()
    {
        var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite).Options);
        await db.Database.EnsureCreatedAsync();
        var adminRole = new Role { Name = "admin", Description = "admin" };
        var managerRole = new Role
        {
            Name = "manager",
            Description = "manager",
            Permissions = { new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own } },
        };
        var admin = new User { Email = "a@x.test", FullName = "Админ", PasswordHash = "x", Role = adminRole, IsActive = true, IsApproved = true };
        var manager = new User { Email = "m@x.test", FullName = "Аня", PasswordHash = "x", Role = managerRole, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Иванов Иван", Phone = "+7 (900) 111-22-33" };
        db.AddRange(admin, manager, client);
        await db.SaveChangesAsync();
        return new Fixture(db, new SalesRequestService(db), manager, admin, client);
    }

    private static SalesRequestCreateInput NewRequest(Client client, string source = "Call")
        => new(source, client.Id, null, null, null, null, null, "Спор с застройщиком", null, null, null);

    private static int StatusId(PaymentContext db, string key) => db.SalesRequestStatuses.Single(item => item.Key == key).Id;

    [Fact]
    public async Task Create_ValidatesFieldsCreatesFirstStatusTaskAndEvent()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;

        Assert.False((await f.Service.CreateAsync(NewRequest(f.Client, "Радио"), f.Manager.Id, default)).Ok);
        Assert.False((await f.Service.CreateAsync(NewRequest(f.Client) with { Summary = "  " }, f.Manager.Id, default)).Ok);

        var created = await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default);

        Assert.True(created.Ok);
        var request = created.Value!;
        Assert.Equal("Новая", f.Db.SalesRequestStatuses.Single(s => s.Id == request.StatusId).Title);
        Assert.Equal(f.Manager.Id, request.ResponsibleUserId);
        Assert.Equal("New", request.Type);
        Assert.Contains("Клиент без истории дел", request.Comment);
        var task = Assert.Single(f.Db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id));
        Assert.Equal(f.Manager.Id, task.AssigneeUserId);
        Assert.Equal(["Created", "TaskCreated"], f.Db.SalesRequestEvents.OrderBy(e => e.Id).Select(e => e.Kind).ToList());
    }

    [Fact]
    public async Task NewClient_IsCreatedOnce_AndPhoneDuplicateIsBlocked()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var input = new SalesRequestCreateInput("Avito", null, "Петров Пётр", "8 900 555 44 33", null, null, null, "Развод", null, null, null);

        var first = await f.Service.CreateAsync(input, f.Manager.Id, default);
        var duplicate = await f.Service.CreateAsync(input with { NewClientName = "Петров П.", NewClientPhone = "+7 (900) 555-44-33" }, f.Manager.Id, default);
        var existingByPhone = await f.Service.CreateAsync(input with { NewClientName = "Иван", NewClientPhone = "89001112233" }, f.Manager.Id, default);

        Assert.True(first.Ok);
        Assert.Equal(409, duplicate.Status);
        Assert.Equal(409, existingByPhone.Status);
        Assert.Equal(1, await f.Db.Clients.CountAsync(c => c.Name == "Петров Пётр"));
        Assert.False((await f.Service.CreateAsync(input with { NewClientName = "Коротко", NewClientPhone = "123" }, f.Manager.Id, default)).Ok);
    }

    [Fact]
    public async Task CaseLink_RequiresClientsCase_AndMarksClosedCase()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var other = new Client { Name = "Другой", Phone = "+79000000000" };
        var closed = new ClientCase { ClientId = f.Client.Id, Status = ClientCaseStatus.Closed, CaseNumber = "87", Title = "Старое" };
        var foreign = new ClientCase { ClientId = other.Id, CaseNumber = "88", Title = "Чужое" };
        f.Db.AddRange(other, closed);
        await f.Db.SaveChangesAsync();
        foreign.ClientId = other.Id;
        f.Db.ClientCases.Add(foreign);
        await f.Db.SaveChangesAsync();

        var ok = await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = closed.Id }, f.Manager.Id, default);
        var denied = await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = foreign.Id }, f.Manager.Id, default);

        Assert.True(ok.Value!.LinkedCaseWasClosed);
        Assert.Equal("PerCase", ok.Value.Type);
        Assert.Equal(404, denied.Status);
    }

    [Fact]
    public async Task Transition_ClosesPreviousStatusTasksAndCreatesNextOnes()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;

        var moved = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "qualification"), request.Version, null, false, null), f.Manager.Id, default);

        Assert.True(moved.Ok);
        var tasks = f.Db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id).OrderBy(t => t.Id).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.Equal("done", tasks[0].Status);
        Assert.Equal("new", tasks[1].Status);
        Assert.Contains("Квалифицировать", tasks[1].Title);
    }

    [Fact]
    public async Task StaleVersion_IsRejected_AndFinalRequestDoesNotMove()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var version = request.Version;
        await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "qualification"), version, null, false, null), f.Manager.Id, default);

        var stale = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "calculation"), version, null, false, null), f.Manager.Id, default);
        var fresh = (await f.Db.SalesRequests.FindAsync(request.Id))!;
        var declined = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "declined"), fresh.Version, null, false, null), f.Manager.Id, default);
        var afterFinal = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "calculation"), declined.Value!.Version, null, false, null), f.Manager.Id, default);

        Assert.Equal(409, stale.Status);
        Assert.Equal("Failure", declined.Value.FinalKind);
        Assert.Equal(409, afterFinal.Status);
    }

    [Fact]
    public async Task FinalStatus_BlocksOnForeignOpenTasks_AndForceCloseMarksThemCancelled()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Service.CreateManualTaskAsync(request.Id, f.Admin.Id, "Уточнить реквизиты", null, f.Manager.Id, default);
        var target = StatusId(f.Db, "abandoned");

        var blocked = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, null, false, null), f.Manager.Id, default);
        var forced = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, null, true, null), f.Manager.Id, default);

        Assert.Equal(409, blocked.Status);
        Assert.NotNull(blocked.Details);
        Assert.True(forced.Ok);
        var manual = f.Db.TaskJournalItems.Single(t => t.Title == "Уточнить реквизиты");
        Assert.Equal("cancelled", manual.Status);
        Assert.Contains("принудительно", manual.Result);
        Assert.Contains(f.Db.SalesRequestEvents, e => e.Kind == "TasksForceClosed");
        Assert.Contains(f.Db.TaskJournalItems, t => t.Title.StartsWith("Дожим") && t.Status == "new");
        Assert.NotNull(forced.Value!.ClosedAt);
    }

    [Fact]
    public async Task Paid_RequiresRealPaymentOfSameClient()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var other = new Client { Name = "Другой", Phone = "+79000000001" };
        f.Db.Clients.Add(other);
        await f.Db.SaveChangesAsync();
        var unpaid = new Payment { ClientId = f.Client.Id, Amount = 100m, PaidAmount = 0m, Description = "Счёт" };
        var foreign = new Payment { ClientId = other.Id, Amount = 100m, PaidAmount = 100m, Description = "Чужой" };
        var paid = new Payment { ClientId = f.Client.Id, Amount = 100m, PaidAmount = 100m, Description = "Оплачен" };
        f.Db.Payments.AddRange(unpaid, foreign, paid);
        await f.Db.SaveChangesAsync();
        var target = StatusId(f.Db, "paid");

        Assert.False((await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, null, true, null), f.Manager.Id, default)).Ok);
        Assert.Equal(404, (await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, foreign.Id, true, null), f.Manager.Id, default)).Status);
        Assert.Equal(409, (await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, unpaid.Id, true, null), f.Manager.Id, default)).Status);
        var done = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, paid.Id, true, null), f.Manager.Id, default);

        Assert.True(done.Ok);
        Assert.Equal("Success", done.Value!.FinalKind);
        Assert.Equal(paid.Id, done.Value.PaidPaymentId);
    }

    [Fact]
    public async Task Report_CountsConversionBySource()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var won = (await f.Service.CreateAsync(NewRequest(f.Client, "Avito") with { Amount = 500m }, f.Manager.Id, default)).Value!;
        await f.Service.CreateAsync(NewRequest(f.Client, "Avito"), f.Manager.Id, default);
        var lost = (await f.Service.CreateAsync(NewRequest(f.Client, "Call") with { Amount = 70m }, f.Manager.Id, default)).Value!;
        var payment = new Payment { ClientId = f.Client.Id, Amount = 500m, PaidAmount = 500m, Description = "x" };
        f.Db.Payments.Add(payment);
        await f.Db.SaveChangesAsync();
        await f.Service.TransitionAsync(won.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), won.Version, payment.Id, true, null), f.Manager.Id, default);
        await f.Service.TransitionAsync(lost.Id, new SalesRequestTransitionInput(StatusId(f.Db, "declined"), lost.Version, null, true, null), f.Manager.Id, default);

        var report = await f.Service.BuildReportAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, default);

        Assert.Equal(3, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.InProgress);
        Assert.Equal(33.3m, report.ConversionPercent);
        Assert.Equal(500m, report.WonAmount);
        Assert.Equal(70m, report.LostAmount);
        Assert.Equal(50m, report.BySource.Single(s => s.Source == "Avito").ConversionPercent);
    }

    private static SalesRequestsController ControllerFor(PaymentContext db, User user, bool admin = false)
        => new(db, new SalesRequestService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, admin ? "admin" : "user")], "Test")),
                },
            },
        };

    [Fact]
    public async Task Controller_DeniesRolesWithoutSection_AndHidesForeignRequestsInOwnScope()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var lawyerRole = new Role { Name = "lawyer", Description = "lawyer", Permissions = { new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All } } };
        var lawyer = new User { Email = "l@x.test", FullName = "Юрист", PasswordHash = "x", Role = lawyerRole, IsActive = true, IsApproved = true };
        f.Db.Users.Add(lawyer);
        await f.Db.SaveChangesAsync();
        var mine = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var adminsOwn = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;

        var lawyerList = await ControllerFor(f.Db, lawyer).List(null, null, null, null, null, 1, 30, default);
        var lawyerCreate = await ControllerFor(f.Db, lawyer).Create(new SalesRequestCreateRequest { Source = "Call", ClientId = f.Client.Id, Summary = "x" }, default);
        var managerList = await ControllerFor(f.Db, f.Manager).List(null, null, null, null, null, 1, 30, default);
        var managerForeign = await ControllerFor(f.Db, f.Manager).Get(adminsOwn.Id, default);
        var managerOwn = await ControllerFor(f.Db, f.Manager).Get(mine.Id, default);
        var adminList = await ControllerFor(f.Db, f.Admin, admin: true).List(null, null, null, null, null, 1, 30, default);

        Assert.IsType<ForbidResult>(lawyerList.Result);
        Assert.IsType<ForbidResult>(lawyerCreate.Result);
        var managerJson = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(managerList.Result).Value);
        Assert.Contains($"\"Id\":{mine.Id},", managerJson);
        Assert.DoesNotContain($"\"Id\":{adminsOwn.Id},", managerJson);
        Assert.IsType<NotFoundResult>(managerForeign.Result);
        Assert.IsType<OkObjectResult>(managerOwn.Result);
        Assert.Contains("\"total\":2", System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(adminList.Result).Value));
    }

    [Fact]
    public async Task Paid_CreatesCaseOnceForNewInquiry_AndNotForRequestLinkedToCase()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var payment = new Payment { ClientId = f.Client.Id, Amount = 100m, PaidAmount = 100m, Description = "x" };
        var existing = new ClientCase { ClientId = f.Client.Id, Status = ClientCaseStatus.Open, CaseNumber = "5", Title = "Текущее" };
        f.Db.AddRange(payment, existing);
        await f.Db.SaveChangesAsync();
        var fresh = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var linked = (await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = existing.Id }, f.Manager.Id, default)).Value!;
        var paid = StatusId(f.Db, "paid");

        var casesBefore = await f.Db.ClientCases.CountAsync();
        var done = await f.Service.TransitionAsync(fresh.Id, new SalesRequestTransitionInput(paid, fresh.Version, payment.Id, true, null), f.Manager.Id, default);
        var linkedDone = await f.Service.TransitionAsync(linked.Id, new SalesRequestTransitionInput(paid, linked.Version, payment.Id, true, null), f.Manager.Id, default);

        Assert.True(done.Ok);
        Assert.NotNull(done.Value!.ResultClientCaseId);
        var created = await f.Db.ClientCases.FindAsync(done.Value.ResultClientCaseId);
        Assert.Equal(f.Client.Id, created!.ClientId);
        Assert.Equal("Спор с застройщиком", created.Title);
        Assert.Equal(casesBefore + 1, await f.Db.ClientCases.CountAsync());
        Assert.Null(linkedDone.Value!.ResultClientCaseId);
        Assert.Contains(f.Db.SalesRequestEvents, e => e.Kind == "CaseCreated" && e.SalesRequestId == fresh.Id);
        var lawyerTask = f.Db.TaskJournalItems.Single(t => t.SalesRequestId == fresh.Id && t.Title.StartsWith("Назначить юриста"));
        Assert.Equal(created.Id, lawyerTask.ClientCaseId);
    }

    [Fact]
    public async Task FailedPaidTransition_DoesNotLeaveCaseBehind()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var payment = new Payment { ClientId = f.Client.Id, Amount = 100m, PaidAmount = 100m, Description = "x" };
        f.Db.Payments.Add(payment);
        await f.Db.SaveChangesAsync();
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE SalesRequests SET Version = Version + 1 WHERE Id = {0}", request.Id);
        var before = await f.Db.ClientCases.CountAsync();

        var stale = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, payment.Id, true, null), f.Manager.Id, default);

        Assert.Equal(409, stale.Status);
        Assert.Equal(before, await f.Db.ClientCases.CountAsync());
    }

    [Fact]
    public async Task ReplaceRules_ValidatesAndAffectsOnlyFutureTransitions()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var qualification = StatusId(f.Db, "qualification");

        Assert.False((await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput(" ", null, 10)], default)).Ok);
        Assert.False((await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("Позвонить", null, 0)], default)).Ok);
        Assert.Equal(404, (await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("Позвонить", 9999, 10)], default)).Status);
        var ok = await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("Позвонить клиенту", f.Admin.Id, 15), new SalesRequestRuleInput("Проверить документы", null, 60)], default);

        Assert.True(ok.Ok);
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(qualification, request.Version, null, false, null), f.Manager.Id, default);
        var tasks = f.Db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id && t.Title.Contains("Позвонить") || t.Title.Contains("Проверить")).ToList();
        Assert.Equal(f.Admin.Id, tasks.Single(t => t.Title.StartsWith("Позвонить")).AssigneeUserId);
        Assert.Equal(f.Manager.Id, tasks.Single(t => t.Title.StartsWith("Проверить")).AssigneeUserId);
    }

    [Fact]
    public async Task SaveStatus_CreatesRenamesAndProtectsFunnelInvariants()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);

        var created = await f.Service.SaveStatusAsync(null, "Ожидание документов", null, null, null, default);
        var renamed = await f.Service.SaveStatusAsync(created.Value!.Id, "Ждём документы", null, null, null, default);
        var changeFinal = await f.Service.SaveStatusAsync(StatusId(f.Db, "paid"), null, "Failure", null, null, default);
        var blank = await f.Service.SaveStatusAsync(null, " ", null, null, null, default);
        var paid = StatusId(f.Db, "paid");
        var disablePaid = await f.Service.SaveStatusAsync(paid, null, null, null, false, default);
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var disableNew = await f.Service.SaveStatusAsync(request.StatusId, null, null, null, false, default);

        Assert.Equal("Ждём документы", renamed.Value!.Title);
        Assert.Equal(409, changeFinal.Status);
        Assert.False(blank.Ok);
        Assert.Equal(409, disablePaid.Status);
        Assert.Equal(409, disableNew.Status);
    }

    [Fact]
    public async Task AdvanceCompleted_MovesRequestOnlyWhenAllStageTasksAreDone()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "documents-prep"), request.Version, null, false, null), f.Manager.Id, default);
        var stage = f.Db.TaskJournalItems.Single(t => t.SalesRequestId == request.Id && t.Title.StartsWith("Подготовить счёт"));

        Assert.Equal(0, await f.Service.AdvanceCompletedAsync(default));

        stage.Status = "cancelled";
        await f.Db.SaveChangesAsync();
        Assert.Equal(0, await f.Service.AdvanceCompletedAsync(default));

        stage.Status = "done";
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Service.AdvanceCompletedAsync(default));

        var moved = await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.Equal(StatusId(f.Db, "documents-sent"), moved.StatusId);
        Assert.Contains(f.Db.TaskJournalItems, t => t.SalesRequestId == request.Id && t.Title.StartsWith("Отправить документы") && t.Status == "new");
        Assert.Equal(0, await f.Service.AdvanceCompletedAsync(default));
    }

    [Fact]
    public async Task SaveStatus_ConfiguresAndValidatesAutoAdvance()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var calc = StatusId(f.Db, "calculation");

        var self = await f.Service.SaveStatusAsync(calc, null, null, null, null, default, calc);
        var fromFinal = await f.Service.SaveStatusAsync(StatusId(f.Db, "paid"), null, null, null, null, default, calc);
        var ok = await f.Service.SaveStatusAsync(calc, null, null, null, null, default, StatusId(f.Db, "approval"));
        var configured = ok.Value!.AutoAdvanceToStatusId;
        var cleared = await f.Service.SaveStatusAsync(calc, null, null, null, null, default, clearAutoAdvance: true);

        Assert.False(self.Ok);
        Assert.False(fromFinal.Ok);
        Assert.Equal(StatusId(f.Db, "approval"), configured);
        Assert.Null(cleared.Value!.AutoAdvanceToStatusId);
    }
}
