using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DeadlineChangeConcurrencyTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task ConcurrentDeadlineDecisions_OnlyOneDecisionAndNotificationWin(bool postgres, bool slowApprove, bool fastApprove)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_deadline_race_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString); if (!postgres) await keeper.OpenAsync();
        PaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            if (postgres)
            {
                var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString);
                if (gate is not null) builder.AddInterceptors(gate);
                return new PostgreSqlPaymentContext(builder.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (gate is not null) sqlite.AddInterceptors(gate);
            return new PaymentContext(sqlite.Options);
        }
        await using var setup = CreateContext(); if (!postgres) await setup.Database.EnsureCreatedAsync();
        var author = new User { Email = "author@example.test", FullName = "Автор", PasswordHash = "test", IsActive = true, IsApproved = true, Role = new Role { Name = "admin", Description = "Synthetic" } };
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = author, AuthorName = author.FullName, DueAt = due, Status = "inProgress" };
        var request = new TaskJournalDeadlineChangeRequest { TaskJournalItem = task, RequestedByName = "Исполнитель", ReviewerName = author.FullName, Reason = "Нужен срок", CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Status = "pending" };
        setup.TaskJournalDeadlineChangeRequests.Add(request); await setup.SaveChangesAsync();
        var gate = new PauseDecisionSave(); await using var slowDb = CreateContext(gate); await using var fastDb = CreateContext();
        var slowNotifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        var fastNotifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        TasksController Controller(PaymentContext db, INotificationService notifications) => new(db, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, author.Id.ToString()), new Claim(ClaimTypes.Name, author.FullName)], "test")) } },
        };
        Task<ActionResult<TaskJournalItemDto>> Decide(TasksController controller, bool approve, int days) => approve
            ? controller.ApproveDeadlineChangeRequest(task.Id, request.Id, new ApproveTaskJournalDeadlineChangeRequest { ApprovedDueAt = due.AddDays(days), Comment = "Решение" }, CancellationToken.None)
            : controller.RejectDeadlineChangeRequest(task.Id, request.Id, new RejectTaskJournalDeadlineChangeRequest { Comment = "Причина" }, CancellationToken.None);
        var slow = Decide(Controller(slowDb, slowNotifications), slowApprove, 2);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<OkObjectResult>((await Decide(Controller(fastDb, fastNotifications), fastApprove, 3)).Result); }
        finally { gate.Release.TrySetResult(); }
        var late = await slow.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsType<ConflictObjectResult>(late.Result);
        Assert.Empty(((EmptyNotifications)(object)slowNotifications).DecisionCalls);
        Assert.Equal([fastApprove ? nameof(INotificationService.NotifyTaskDeadlineChangeApprovedAsync) : nameof(INotificationService.NotifyTaskDeadlineChangeRejectedAsync)], ((EmptyNotifications)(object)fastNotifications).DecisionCalls);
        setup.ChangeTracker.Clear();
        var savedRequest = await setup.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        var savedTask = await setup.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(fastApprove ? "approved" : "rejected", savedRequest.Status);
        Assert.Equal(fastApprove ? due.AddDays(3) : due, DateTime.SpecifyKind(savedTask.DueAt, DateTimeKind.Utc));
        Assert.Single(await setup.TaskJournalComments.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDeadlineRequests_OnlyOnePendingRequestCanBeCreated(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_deadline_create_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString); if (!postgres) await keeper.OpenAsync();
        PaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            if (postgres)
            {
                var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString);
                if (gate is not null) builder.AddInterceptors(gate);
                return new PostgreSqlPaymentContext(builder.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (gate is not null) sqlite.AddInterceptors(gate);
            return new PaymentContext(sqlite.Options);
        }
        await using var setup = CreateContext(); if (!postgres) await setup.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin", Description = "Synthetic" };
        var author = new User { Email = "deadline-author@example.test", FullName = "Автор", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var worker = new User { Email = "deadline-worker@example.test", FullName = "Исполнитель", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = author, AuthorName = author.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        var gate = new PauseNewDeadline(); await using var slowDb = CreateContext(gate); await using var fastDb = CreateContext();
        var slowNotifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        var fastNotifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        ((EmptyNotifications)(object)slowNotifications).OnRequest = () => Assert.Null(slowDb.Database.CurrentTransaction);
        TasksController Controller(PaymentContext db, INotificationService notifications) => new(db, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()), new Claim(ClaimTypes.Name, worker.FullName)], "test")) } },
        };
        var slow = Controller(slowDb, slowNotifications).CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Первая причина" }, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var fast = await Controller(fastDb, fastNotifications).CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(3), Reason = "Вторая причина" }, CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(fast.Result).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        var retry = await Controller(fastDb, fastNotifications).CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(3), Reason = "Вторая причина" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(retry.Result);
        Assert.Equal([nameof(INotificationService.NotifyTaskDeadlineChangeRequestedAsync)], ((EmptyNotifications)(object)slowNotifications).RequestCalls);
        Assert.Empty(((EmptyNotifications)(object)fastNotifications).RequestCalls);
        setup.ChangeTracker.Clear();
        var saved = Assert.Single(await setup.TaskJournalDeadlineChangeRequests.Where(item => item.TaskJournalItemId == task.Id && item.Status == "pending").ToListAsync());
        Assert.Equal("Первая причина", saved.Reason);
        Assert.Single(await setup.TaskJournalComments.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("role")]
    [InlineData("user")]
    public async Task DeadlineRequest_SerializesPermissionRevocationWithSave_Postgres(string target)
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (gate is not null) builder.AddInterceptors(gate);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        await using var setup = CreateContext();
        var permission = new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All };
        var role = new Role { Name = "deadline-worker", Description = "Synthetic", Permissions = { permission } };
        var author = new User { Email = "revoke-author@example.test", FullName = "Автор", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var worker = new User { Email = "revoke-worker@example.test", FullName = "Исполнитель", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = author, AuthorName = author.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        var gate = new PauseNewDeadline();
        await using var slowDb = CreateContext(gate);
        await using var concurrentDb = CreateContext();
        var notifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        TasksController Controller(PaymentContext db) => new(db, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()), new Claim(ClaimTypes.Name, worker.FullName)], "test")) } },
        };
        var request = new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" };
        var slow = Controller(slowDb).CreateDeadlineChangeRequest(task.Id, request, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            async Task LockTarget() => _ = target switch
            {
                "permission" => await concurrentDb.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"Id\" = {permission.Id} FOR UPDATE NOWAIT"),
                "role" => await concurrentDb.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = {role.Id} FOR UPDATE NOWAIT"),
                _ => await concurrentDb.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {worker.Id} FOR UPDATE NOWAIT"),
            };
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(LockTarget);
            Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        permission.CanView = false;
        await setup.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await Controller(concurrentDb).CreateDeadlineChangeRequest(task.Id, request, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task DeadlineRequest_RevocationStartedFirst_DoesNotCreateOrNotify_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext CreateContext() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var setup = CreateContext();
        var permission = new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All };
        var role = new Role { Name = "deadline-revoke-first", Permissions = { permission } };
        var author = new User { Email = "revoke-first-author@example.test", FullName = "Автор", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var worker = new User { Email = "revoke-first-worker@example.test", FullName = "Исполнитель", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = author, AuthorName = author.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        await using var other = CreateContext();
        var notifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        var controller = new TasksController(other, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()), new Claim(ClaimTypes.Name, worker.FullName)], "test")) } },
        };
        var request = new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" };
        await using (var transaction = await setup.Database.BeginTransactionAsync())
        {
            permission.CanView = false; await setup.SaveChangesAsync();
            var during = await controller.CreateDeadlineChangeRequest(task.Id, request, CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(during.Result).StatusCode);
            await transaction.CommitAsync();
        }
        Assert.IsType<ForbidResult>((await controller.CreateDeadlineChangeRequest(task.Id, request, CancellationToken.None)).Result);
        Assert.Empty(((EmptyNotifications)(object)notifications).RequestCalls);
        Assert.Empty(await setup.TaskJournalDeadlineChangeRequests.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
        Assert.Empty(await setup.TaskJournalComments.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }

    [Fact]
    public async Task DeadlineRequests_DifferentTasksForAdminDoNotBlockEachOther_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (gate is not null) builder.AddInterceptors(gate);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        await using var setup = CreateContext();
        var role = new Role { Name = "admin" };
        var author = new User { Email = "independent-author@example.test", FullName = "Автор", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var worker = new User { Email = "independent-worker@example.test", FullName = "Исполнитель", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        TaskJournalItem TaskItem(string title) => new() { Title = title, ObjectName = "Тест", AuthorUser = author, AuthorName = author.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        var first = TaskItem("Первая"); var second = TaskItem("Вторая"); setup.TaskJournalItems.AddRange(first, second); await setup.SaveChangesAsync();
        var gate = new PauseNewDeadline(); await using var slowDb = CreateContext(gate); await using var fastDb = CreateContext();
        TasksController Controller(PaymentContext db) => new(db, DispatchProxy.Create<INotificationService, EmptyNotifications>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()), new Claim(ClaimTypes.Name, worker.FullName)], "test")) } },
        };
        var request = new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" };
        var slow = Controller(slowDb).CreateDeadlineChangeRequest(first.Id, request, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<CreatedResult>((await Controller(fastDb).CreateDeadlineChangeRequest(second.Id, request, CancellationToken.None)).Result); }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        Assert.Equal(2, await setup.TaskJournalDeadlineChangeRequests.CountAsync());
    }

    [Theory]
    [InlineData("request")]
    [InlineData("responsible")]
    [InlineData("creator")]
    public async Task DeadlineRequest_SerializesSalesRequestDepartmentChanges_Postgres(string target)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (gate is not null) builder.AddInterceptors(gate);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        await using var setup = CreateContext();
        var role = new Role { Name = "sales-deadline-worker", Permissions = {
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = DataVisibilityScope.Department },
        } };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Department = "Отдел A", Role = role };
        var worker = Person("sales-worker@example.test", "Исполнитель");
        var responsible = Person("sales-responsible@example.test", "Ответственный");
        var creator = Person("sales-creator@example.test", "Создатель");
        setup.Users.AddRange(worker, responsible, creator); await setup.SaveChangesAsync();
        var status = new SalesRequestStatus { Key = "deadline-test", Title = "Тест" };
        setup.SalesRequestStatuses.Add(status); await setup.SaveChangesAsync();
        var sales = new SalesRequest { Summary = "Тестовая заявка", StatusId = status.Id, ResponsibleUserId = responsible.Id, CreatedByUserId = creator.Id };
        setup.SalesRequests.Add(sales); await setup.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", SalesRequestId = sales.Id, AuthorUser = responsible, AuthorName = responsible.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        var gate = new PauseNewDeadline(); await using var slowDb = CreateContext(gate); await using var concurrentDb = CreateContext();
        var controller = new TasksController(slowDb, DispatchProxy.Create<INotificationService, EmptyNotifications>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()), new Claim(ClaimTypes.Name, worker.FullName)], "test")) } },
        };
        var slow = controller.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" }, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            async Task LockTarget() => _ = target switch
            {
                "request" => await concurrentDb.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SalesRequests\" WHERE \"Id\" = {sales.Id} FOR UPDATE NOWAIT"),
                "responsible" => await concurrentDb.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {responsible.Id} FOR UPDATE NOWAIT"),
                _ => await concurrentDb.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {creator.Id} FOR UPDATE NOWAIT"),
            };
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(LockTarget);
            Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
    }

    [Fact]
    public async Task DeadlineRequest_DepartmentChangeFirst_RejectsWithoutHistoryOrNotification_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext CreateContext() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var setup = CreateContext();
        var role = new Role { Name = "sales-deadline-revoked", Permissions = {
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = DataVisibilityScope.Department },
        } };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Department = "Отдел A", Role = role };
        var worker = Person("dept-worker@example.test", "Исполнитель"); var responsible = Person("dept-responsible@example.test", "Ответственный"); var creator = Person("dept-creator@example.test", "Создатель");
        setup.Users.AddRange(worker, responsible, creator); await setup.SaveChangesAsync();
        var status = new SalesRequestStatus { Key = "dept-test", Title = "Тест" }; setup.SalesRequestStatuses.Add(status); await setup.SaveChangesAsync();
        var sales = new SalesRequest { Summary = "Тестовая заявка", StatusId = status.Id, ResponsibleUserId = responsible.Id, CreatedByUserId = creator.Id }; setup.SalesRequests.Add(sales); await setup.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", SalesRequestId = sales.Id, AuthorUser = responsible, AuthorName = responsible.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" }; setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        await using var other = CreateContext(); var notifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        var controller = new TasksController(other, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()), new Claim(ClaimTypes.Name, worker.FullName)], "test")) } },
        };
        var request = new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" };
        await using (var transaction = await setup.Database.BeginTransactionAsync())
        {
            responsible.Department = "Отдел B"; creator.Department = "Отдел B"; await setup.SaveChangesAsync();
            var during = await controller.CreateDeadlineChangeRequest(task.Id, request, CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(during.Result).StatusCode);
            await transaction.CommitAsync();
        }
        Assert.IsType<NotFoundResult>((await controller.CreateDeadlineChangeRequest(task.Id, request, CancellationToken.None)).Result);
        Assert.Empty(((EmptyNotifications)(object)notifications).RequestCalls);
        Assert.Empty(await setup.TaskJournalDeadlineChangeRequests.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
        Assert.Empty(await setup.TaskJournalComments.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeadlineRequest_ConcurrentAuthorChange_UsesCurrentReviewer_Postgres(bool changeFirst)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (gate is not null) builder.AddInterceptors(gate);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        await using var setup = CreateContext();
        var role = new Role { Name = "admin" };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var oldAuthor = Person("old-reviewer@example.test", "Прежний постановщик");
        var newAuthor = Person("new-reviewer@example.test", "Новый постановщик");
        var worker = Person("reviewer-worker@example.test", "Исполнитель");
        var admin = Person("reviewer-admin@example.test", "Администратор");
        setup.Users.AddRange(oldAuthor, newAuthor, worker, admin); await setup.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Внутренняя задача", AuthorUser = oldAuthor, AuthorName = oldAuthor.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        var gate = changeFirst ? (SaveChangesInterceptor)new PauseTaskUpdate() : new PauseNewDeadline();
        await using var slowDb = CreateContext(gate); await using var fastDb = CreateContext();
        TasksController Controller(PaymentContext db, User actor) => new(db, DispatchProxy.Create<INotificationService, EmptyNotifications>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Name, actor.FullName)], "test")) } },
        };
        var deadline = new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" };
        var update = new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = newAuthor.Id, DueAt = due, Status = "inProgress" };
        if (changeFirst)
        {
            var barrier = (PauseTaskUpdate)gate;
            var slow = Controller(slowDb, admin).UpdateTask(task.Id, update, CancellationToken.None);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            try
            {
                var during = await Controller(fastDb, worker).CreateDeadlineChangeRequest(task.Id, deadline, CancellationToken.None);
                Assert.Equal(423, Assert.IsType<ObjectResult>(during.Result).StatusCode);
            }
            finally { barrier.Release.TrySetResult(); }
            Assert.IsType<OkObjectResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
            Assert.IsType<CreatedResult>((await Controller(fastDb, worker).CreateDeadlineChangeRequest(task.Id, deadline, CancellationToken.None)).Result);
        }
        else
        {
            var barrier = (PauseNewDeadline)gate;
            var slow = Controller(slowDb, worker).CreateDeadlineChangeRequest(task.Id, deadline, CancellationToken.None);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var concurrent = Controller(fastDb, admin).UpdateTask(task.Id, update, CancellationToken.None);
            try { await Task.Delay(300); Assert.False(concurrent.IsCompleted, "Author update must wait for the task lock."); }
            finally { barrier.Release.TrySetResult(); }
            Assert.IsType<CreatedResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
            Assert.IsType<OkObjectResult>((await concurrent.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        }
        setup.ChangeTracker.Clear();
        var savedTask = await setup.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        var savedRequest = await setup.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id && item.Status == "pending");
        Assert.Equal(newAuthor.Id, savedTask.AuthorUserId);
        Assert.Equal(newAuthor.Id, savedRequest.ReviewerUserId);
        Assert.Equal(newAuthor.FullName, savedRequest.ReviewerName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeadlineRequest_ConcurrentAuthorChange_UsesCurrentReviewer_Sqlite(bool changeFirst)
    {
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_author_race_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString); await keeper.OpenAsync();
        PaymentContext CreateContext(SaveChangesInterceptor? gate = null)
        {
            var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (gate is not null) builder.AddInterceptors(gate);
            return new PaymentContext(builder.Options);
        }
        await using var setup = CreateContext(); await setup.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin" };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var oldAuthor = Person("old-reviewer@example.test", "Прежний постановщик");
        var newAuthor = Person("new-reviewer@example.test", "Новый постановщик");
        var worker = Person("reviewer-worker@example.test", "Исполнитель");
        var admin = Person("reviewer-admin@example.test", "Администратор");
        setup.Users.AddRange(oldAuthor, newAuthor, worker, admin); await setup.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Внутренняя задача", AuthorUser = oldAuthor, AuthorName = oldAuthor.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        var gate = changeFirst ? (SaveChangesInterceptor)new PauseTaskUpdate() : new PauseNewDeadline();
        await using var slowDb = CreateContext(gate); await using var fastDb = CreateContext();
        TasksController Controller(PaymentContext db, User actor) => new(db, DispatchProxy.Create<INotificationService, EmptyNotifications>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Name, actor.FullName)], "test")) } },
        };
        var deadline = new CreateTaskJournalDeadlineChangeRequest { RequestedDueAt = due.AddDays(2), Reason = "Причина" };
        var update = new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = newAuthor.Id, DueAt = due, Status = "inProgress" };
        if (changeFirst)
        {
            var barrier = (PauseTaskUpdate)gate;
            var slow = Controller(slowDb, admin).UpdateTask(task.Id, update, CancellationToken.None);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            try
            {
                var during = await Controller(fastDb, worker).CreateDeadlineChangeRequest(task.Id, deadline, CancellationToken.None);
                Assert.Equal(423, Assert.IsType<ObjectResult>(during.Result).StatusCode);
            }
            finally { barrier.Release.TrySetResult(); }
            Assert.IsType<OkObjectResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
            Assert.IsType<CreatedResult>((await Controller(fastDb, worker).CreateDeadlineChangeRequest(task.Id, deadline, CancellationToken.None)).Result);
        }
        else
        {
            var barrier = (PauseNewDeadline)gate;
            var slow = Controller(slowDb, worker).CreateDeadlineChangeRequest(task.Id, deadline, CancellationToken.None);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var concurrent = Task.Run(() => Controller(fastDb, admin).UpdateTask(task.Id, update, CancellationToken.None));
            try { await Task.Delay(300); Assert.False(concurrent.IsCompleted, "Author update must wait for the task lock."); }
            finally { barrier.Release.TrySetResult(); }
            Assert.IsType<CreatedResult>((await slow.WaitAsync(TimeSpan.FromSeconds(20))).Result);
            Assert.IsType<OkObjectResult>((await concurrent.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        }
        setup.ChangeTracker.Clear();
        var savedTask = await setup.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        var savedRequest = await setup.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id && item.Status == "pending");
        Assert.Equal(newAuthor.Id, savedTask.AuthorUserId);
        Assert.Equal(newAuthor.Id, savedRequest.ReviewerUserId);
        Assert.Equal(newAuthor.FullName, savedRequest.ReviewerName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorChange_WithPendingDeadline_NotifiesNewReviewerWithoutRepeatingObserver(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var connection = new SqliteConnection("Data Source=:memory:"); if (!postgres) await connection.OpenAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName)).Options);
        if (!postgres) await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin" };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var oldAuthor = Person("notify-old@example.test", "Прежний постановщик"); var newAuthor = Person("notify-new@example.test", "Новый постановщик");
        var worker = Person("notify-worker@example.test", "Исполнитель"); var admin = Person("notify-admin@example.test", "Администратор"); var observer = Person("notify-observer@example.test", "Наблюдатель");
        db.Users.AddRange(oldAuthor, newAuthor, worker, admin, observer); await db.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Внутренняя задача", AuthorUser = oldAuthor, AuthorName = oldAuthor.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress", ChannelsJson = "[\"inApp\"]" };
        task.Participants.Add(new TaskJournalParticipant { UserId = observer.Id, DisplayName = observer.FullName, Role = "observer" });
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        var pending = new TaskJournalDeadlineChangeRequest { TaskJournalItemId = task.Id, RequestedByUserId = worker.Id, RequestedByName = worker.FullName, ReviewerUserId = oldAuthor.Id, ReviewerName = oldAuthor.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Жду документы", Status = "pending" };
        db.TaskJournalDeadlineChangeRequests.Add(pending); await db.SaveChangesAsync();
        var notifications = new NotificationService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(), DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance);
        await notifications.NotifyTaskDeadlineChangeRequestedAsync(task, pending);
        var controller = new TasksController(db, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()), new Claim(ClaimTypes.Name, admin.FullName)], "test")) } },
        };
        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = newAuthor.Id, DueAt = due, Status = "inProgress", Channels = ["inApp"], Participants = [new TaskJournalParticipantRequest { UserId = observer.Id, Role = "observer" }] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        var requestNotifications = await db.UserNotifications.AsNoTracking().Where(item => item.Type == UserNotificationTypes.TaskDeadlineChangeRequested).ToListAsync();
        Assert.Single(requestNotifications, item => item.RecipientUserId == newAuthor.Id);
        Assert.Single(requestNotifications, item => item.RecipientUserId == observer.Id);
        Assert.Contains($"\"deadlineChangeRequestId\":{pending.Id}", requestNotifications.Single(item => item.RecipientUserId == newAuthor.Id).MetadataJson);
        var back = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = oldAuthor.Id, DueAt = due, Status = "inProgress", Channels = ["inApp"], Participants = [new TaskJournalParticipantRequest { UserId = observer.Id, Role = "observer" }] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(back.Result);
        requestNotifications = await db.UserNotifications.AsNoTracking().Where(item => item.Type == UserNotificationTypes.TaskDeadlineChangeRequested).ToListAsync();
        Assert.Equal(2, requestNotifications.Count(item => item.RecipientUserId == oldAuthor.Id));
        Assert.Single(requestNotifications, item => item.RecipientUserId == observer.Id);
        var repeat = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = oldAuthor.Id, DueAt = due, Status = "inProgress", Channels = ["inApp"], Participants = [new TaskJournalParticipantRequest { UserId = observer.Id, Role = "observer" }] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(repeat.Result);
        Assert.Equal(4, await db.UserNotifications.CountAsync(item => item.Type == UserNotificationTypes.TaskDeadlineChangeRequested));
        var assignRequester = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = worker.Id, DueAt = due, Status = "inProgress", Channels = ["inApp"], Participants = [new TaskJournalParticipantRequest { UserId = observer.Id, Role = "observer" }] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(assignRequester.Result);
        requestNotifications = await db.UserNotifications.AsNoTracking().Where(item => item.Type == UserNotificationTypes.TaskDeadlineChangeRequested).ToListAsync();
        Assert.Single(requestNotifications, item => item.RecipientUserId == worker.Id);
        Assert.Single(requestNotifications, item => item.RecipientUserId == observer.Id);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ReviewerAssignment_DoesNotNotifyWithoutCurrentAccess(bool canView, bool active)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName)).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "restricted-reviewer", Permissions = { new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = canView, ViewScope = DataVisibilityScope.Own } } };
        var reviewer = new User { Email = "restricted-reviewer@example.test", FullName = "Постановщик", PasswordHash = "x", IsActive = active, IsApproved = true, Role = role };
        db.Users.Add(reviewer); await db.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = reviewer, AuthorName = reviewer.FullName, DueAt = due, Status = "inProgress", ChannelsJson = "[\"inApp\",\"email\"]" };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        var pending = new TaskJournalDeadlineChangeRequest { TaskJournalItemId = task.Id, RequestedByUserId = null, RequestedByName = "Исполнитель", ReviewerUserId = reviewer.Id, ReviewerName = reviewer.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Причина", Status = "pending", UpdatedAt = DateTime.UtcNow };
        db.TaskJournalDeadlineChangeRequests.Add(pending); await db.SaveChangesAsync();
        var service = new NotificationService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(), DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance);
        Assert.Empty(await service.NotifyTaskDeadlineReviewerAssignedAsync(task, pending));
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("reviewer")]
    [InlineData("version")]
    public async Task ReviewerAssignment_DoesNotNotifyForStaleRequest(string mutation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName)).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin" };
        var reviewer = new User { Email = "stale-reviewer@example.test", FullName = "Постановщик", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var replacement = new User { Email = "replacement-reviewer@example.test", FullName = "Другой", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        db.Users.AddRange(reviewer, replacement); await db.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = reviewer, AuthorName = reviewer.FullName, DueAt = due, Status = "inProgress", ChannelsJson = "[\"inApp\",\"email\"]" };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        var pending = new TaskJournalDeadlineChangeRequest { TaskJournalItemId = task.Id, RequestedByName = "Исполнитель", ReviewerUserId = reviewer.Id, ReviewerName = reviewer.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Причина", Status = "pending", UpdatedAt = DateTime.UtcNow };
        db.TaskJournalDeadlineChangeRequests.Add(pending); await db.SaveChangesAsync();
        if (mutation == "approved") await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "approved"));
        else if (mutation == "reviewer") await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerUserId, replacement.Id));
        else await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, pending.UpdatedAt!.Value.AddSeconds(1)));
        var service = new NotificationService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(), DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance);
        Assert.Empty(await service.NotifyTaskDeadlineReviewerAssignedAsync(task, pending));
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewerNotification_FailureAfterAuthorChange_IsRetriedWithoutChangingAuthorAgain(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var connection = new SqliteConnection("Data Source=:memory:"); if (!postgres) await connection.OpenAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName)).Options);
        if (!postgres) await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin" };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var oldAuthor = Person("retry-old@example.test", "Прежний постановщик"); var newAuthor = Person("retry-new@example.test", "Новый постановщик");
        var worker = Person("retry-worker@example.test", "Исполнитель"); var admin = Person("retry-admin@example.test", "Администратор");
        db.Users.AddRange(oldAuthor, newAuthor, worker, admin); await db.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Внутренняя задача", AuthorUser = oldAuthor, AuthorName = oldAuthor.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress", ChannelsJson = "[\"inApp\"]" };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        var pending = new TaskJournalDeadlineChangeRequest { TaskJournalItemId = task.Id, RequestedByUserId = worker.Id, RequestedByName = worker.FullName, ReviewerUserId = oldAuthor.Id, ReviewerName = oldAuthor.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Причина", Status = "pending" };
        db.TaskJournalDeadlineChangeRequests.Add(pending); await db.SaveChangesAsync();
        var notifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        var probe = (EmptyNotifications)(object)notifications; probe.OnReviewerAssigned = () => throw new InvalidOperationException("Simulated delivery failure");
        var controller = new TasksController(db, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()), new Claim(ClaimTypes.Name, admin.FullName)], "test")) } },
        };
        var request = new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = newAuthor.Id, DueAt = due, Status = "inProgress", Channels = ["inApp"] };
        Assert.IsType<OkObjectResult>((await controller.UpdateTask(task.Id, request, CancellationToken.None)).Result);
        db.ChangeTracker.Clear();
        Assert.Equal(newAuthor.Id, (await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).AuthorUserId);
        var queued = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == pending.Id);
        Assert.Equal(newAuthor.Id, queued.ReviewerUserId);
        Assert.True(queued.ReviewerDispatchPending);
        Assert.Equal(1, probe.ReviewerAssignedCalls);
        var expiredLease = DateTime.UtcNow.AddMinutes(-1);
        await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == pending.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)expiredLease));
        probe.OnReviewerAssigned = null;
        var recovery = new TaskDeadlineReviewerDispatchProcessor(db, notifications);
        Assert.True(await recovery.ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(2, probe.ReviewerAssignedCalls);
        Assert.False((await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == pending.Id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewerNotification_RecoveredWhenFailureOccursBeforeFirstAttempt(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null; if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var connection = new SqliteConnection("Data Source=:memory:"); if (!postgres) await connection.OpenAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName)).Options);
        if (!postgres) await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin" };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var oldAuthor = Person("before-old@example.test", "Прежний"); var newAuthor = Person("before-new@example.test", "Новый");
        var worker = Person("before-worker@example.test", "Исполнитель"); var admin = Person("before-admin@example.test", "Администратор");
        db.Users.AddRange(oldAuthor, newAuthor, worker, admin); await db.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Внутренняя задача", AuthorUser = oldAuthor, AuthorName = oldAuthor.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        var pending = new TaskJournalDeadlineChangeRequest { TaskJournalItemId = task.Id, RequestedByUserId = worker.Id, RequestedByName = worker.FullName, ReviewerUserId = oldAuthor.Id, ReviewerName = oldAuthor.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Причина", Status = "pending" };
        db.TaskJournalDeadlineChangeRequests.Add(pending); await db.SaveChangesAsync();
        var notifications = DispatchProxy.Create<INotificationService, EmptyNotifications>(); var probe = (EmptyNotifications)(object)notifications;
        probe.OnTaskChanged = () => throw new InvalidOperationException("Simulated failure before reviewer dispatch");
        var controller = new TasksController(db, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()), new Claim(ClaimTypes.Name, admin.FullName)], "test")) } },
        };
        var request = new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = newAuthor.Id, DueAt = due, Status = "inProgress" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateTask(task.Id, request, CancellationToken.None));
        db.ChangeTracker.Clear();
        var queued = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == pending.Id);
        Assert.Equal(newAuthor.Id, queued.ReviewerUserId); Assert.True(queued.ReviewerDispatchPending); Assert.Equal(0, probe.ReviewerAssignedCalls);
        probe.OnTaskChanged = null;
        Assert.True(await new TaskDeadlineReviewerDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(1, probe.ReviewerAssignedCalls);
        Assert.False((await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == pending.Id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewerNotification_RollsBackWithFailedAuthorChange(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null; if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var connection = new SqliteConnection("Data Source=:memory:"); if (!postgres) await connection.OpenAsync();
        PaymentContext Make(SaveChangesInterceptor? interceptor = null)
        {
            if (postgres)
            {
                var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString);
                if (interceptor is not null) builder.AddInterceptors(interceptor);
                return new PostgreSqlPaymentContext(builder.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) sqlite.AddInterceptors(interceptor);
            return new PaymentContext(sqlite.Options);
        }
        await using var setup = Make(); if (!postgres) await setup.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin" };
        User Person(string email, string name) => new() { Email = email, FullName = name, PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
        var oldAuthor = Person("rollback-old@example.test", "Прежний"); var newAuthor = Person("rollback-new@example.test", "Новый");
        var worker = Person("rollback-worker@example.test", "Исполнитель"); var admin = Person("rollback-admin@example.test", "Администратор");
        setup.Users.AddRange(oldAuthor, newAuthor, worker, admin); await setup.SaveChangesAsync();
        var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem { Title = "Задача", ObjectName = "Внутренняя задача", AuthorUser = oldAuthor, AuthorName = oldAuthor.FullName, AssigneeUser = worker, AssigneeName = worker.FullName, DueAt = due, Status = "inProgress" };
        setup.TaskJournalItems.Add(task); await setup.SaveChangesAsync();
        var pending = new TaskJournalDeadlineChangeRequest { TaskJournalItemId = task.Id, RequestedByUserId = worker.Id, RequestedByName = worker.FullName, ReviewerUserId = oldAuthor.Id, ReviewerName = oldAuthor.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Причина", Status = "pending" };
        setup.TaskJournalDeadlineChangeRequests.Add(pending); await setup.SaveChangesAsync();
        await using var failing = Make(new FailReviewerQueueSave()); var notifications = DispatchProxy.Create<INotificationService, EmptyNotifications>();
        var controller = new TasksController(failing, notifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()), new Claim(ClaimTypes.Name, admin.FullName)], "test")) } },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = worker.Id, AuthorUserId = newAuthor.Id, DueAt = due, Status = "inProgress" }, CancellationToken.None));
        setup.ChangeTracker.Clear();
        Assert.Equal(oldAuthor.Id, (await setup.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).AuthorUserId);
        var saved = await setup.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == pending.Id);
        Assert.Equal(oldAuthor.Id, saved.ReviewerUserId); Assert.False(saved.ReviewerDispatchPending);
        Assert.Equal(0, ((EmptyNotifications)(object)notifications).ReviewerAssignedCalls);
    }

    private sealed class FailReviewerQueueSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskJournalDeadlineChangeRequest>().Any(item => item.State == EntityState.Modified && item.Entity.ReviewerDispatchPending))
                throw new InvalidOperationException("Simulated domain save failure");
            return ValueTask.FromResult(result);
        }
    }

    private class DenyExternalTransport : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("External transports must not be called by this test.");
    }

    private sealed class PauseTaskUpdate : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskJournalItem>().Any(item => item.State == EntityState.Modified))
            { Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class PauseNewDeadline : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskJournalDeadlineChangeRequest>().Any(item => item.State == EntityState.Added))
            { Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class PauseDecisionSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskJournalDeadlineChangeRequest>().Any(item => item.State == EntityState.Modified))
            {
                Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private class EmptyNotifications : DispatchProxy
    {
        public List<string> DecisionCalls { get; } = [];
        public List<string> RequestCalls { get; } = [];
        public Action? OnRequest { get; set; }
        public Action? OnReviewerAssigned { get; set; }
        public Action? OnTaskChanged { get; set; }
        public int ReviewerAssignedCalls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method is null) return null;
            if (method.Name is nameof(INotificationService.NotifyTaskDeadlineChangeApprovedAsync) or nameof(INotificationService.NotifyTaskDeadlineChangeRejectedAsync)) DecisionCalls.Add(method.Name);
            if (method.Name == nameof(INotificationService.NotifyTaskDeadlineChangeRequestedAsync)) { OnRequest?.Invoke(); RequestCalls.Add(method.Name); }
            if (method.Name == nameof(INotificationService.NotifyTaskDeadlineReviewerAssignedAsync)) { ReviewerAssignedCalls++; OnReviewerAssigned?.Invoke(); }
            if (method.Name == nameof(INotificationService.NotifyTaskChangedAsync)) OnTaskChanged?.Invoke();
            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = type.GenericTypeArguments[0]; var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
            }
            return null;
        }
    }
}
