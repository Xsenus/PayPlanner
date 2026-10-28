using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskPlanningFilterParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportUsesSameFiltersAsVisibleJournalAcrossFormats(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var lease = template is not null ? await template.CreateDatabaseAsync() : null;
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_planning_parity_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString);
        if (!postgres) await keeper.OpenAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString).Options);
        if (!postgres) await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "planning-own", Permissions = [new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, CanExport = true, ViewScope = DataVisibilityScope.Own }] };
        var user = new User { Email = "planning-owner@example.test", FullName = "Сотрудник", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var other = new User { Email = "planning-other@example.test", FullName = "Другой", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
        var client = new Client { Name = "Синтетический клиент", IsActive = true };
        db.Users.AddRange(user, other); db.Clients.Add(client); await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        TaskJournalItem Item(string title, User author, bool control, Client? target, string status) => new()
        {
            Title = title, ObjectName = "Синтетический объект", AuthorUserId = author.Id, AuthorName = author.FullName,
            IsUnderControl = control, ClientId = target?.Id, DueAt = now, Status = status,
        };
        var first = Item("Видимая контролируемая", user, true, client, "onApproval");
        first.ClientBillingClassification = TaskClientBillingClassification.Billable;
        first.Participants.Add(new TaskJournalParticipant { UserId = user.Id, Role = "observer", DisplayName = user.FullName });
        first.Participants.Add(new TaskJournalParticipant { UserId = user.Id, Role = "approver", DisplayName = user.FullName, ApprovalStatus = "pending" });
        var second = Item("Видимая завершённая", user, false, client, "done");
        var third = Item("Видимая неразобранная", user, false, null, "waiting");
        third.ClientBillingClassification = TaskClientBillingClassification.Internal;
        var hidden = Item("Чужая скрытая", other, true, client, "waiting");
        db.TaskJournalItems.AddRange(first, second, third, hidden); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var pdf = new CapturePdf();
        var controller = new TasksController(db, DispatchProxy.Create<INotificationService, NoNotifications>(), pdfService: pdf)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.FullName)], "test")) } } };
        var cases = new (Dictionary<string, object?> Filter, string[] Expected)[]
        {
            (new() { ["underControl"] = true }, [first.Title]),
            (new() { ["authorUserId"] = user.Id }, [first.Title, second.Title, third.Title]),
            (new() { ["participantRole"] = "observer" }, [first.Title]),
            (new() { ["approvalScope"] = true }, [first.Title]),
            (new() { ["billingStatus"] = "internal" }, [third.Title]),
            (new() { ["billingStatus"] = "unbilled", ["status"] = "done" }, [first.Title]),
            (new() { ["untriaged"] = true }, [third.Title]),
            (new() { ["metric"] = "active" }, [first.Title, third.Title]),
            (new() { ["metric"] = "today", ["todayFrom"] = now.Date, ["todayTo"] = now.Date.AddDays(1) }, [first.Title, third.Title]),
            (new() { ["assigneeUserId"] = -1 }, []),
        };
        var allTitles = new[] { first.Title, second.Title, third.Title, hidden.Title };
        foreach (var (filter, wanted) in cases)
        {
            var parameters = new Dictionary<string, object?>(filter) { ["ct"] = CancellationToken.None };
            parameters.TryAdd("status", "all");
            var listMethod = typeof(TasksController).GetMethod(nameof(TasksController.GetTasks))!;
            var list = await (Task<ActionResult<PagedResponse<TaskJournalItemDto>>>)listMethod.Invoke(controller, Arguments(listMethod, parameters))!;
            var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(Assert.IsType<OkObjectResult>(list.Result).Value);
            var expected = page.Items.Select(item => item.Title).ToHashSet();
            Assert.Equal(wanted.Order(StringComparer.Ordinal), expected.Order(StringComparer.Ordinal));
            Assert.DoesNotContain(hidden.Title, expected);
            foreach (var format in new[] { "html", "word", "pdf" })
            {
                var reportMethod = typeof(TasksController).GetMethod(nameof(TasksController.ExportPlanningReport))!;
                Assert.All(filter.Keys, key => Assert.Contains(reportMethod.GetParameters(), parameter => parameter.Name == key));
                var reportArgs = new Dictionary<string, object?>(parameters) { ["format"] = format };
                var result = await (Task<IActionResult>)reportMethod.Invoke(controller, Arguments(reportMethod, reportArgs))!;
                var file = Assert.IsType<FileContentResult>(result);
                var text = format == "word" ? ReadWord(file.FileContents) : Encoding.UTF8.GetString(file.FileContents);
                foreach (var title in allTitles) Assert.Equal(expected.Contains(title), text.Contains(title, StringComparison.Ordinal));
            }
        }
        db.TaskJournalItems.AddRange(Enumerable.Range(0, 5001).Select(index =>
        {
            var task = Item($"Граничная задача {index}", user, false, client, "waiting");
            task.Priority = index < 5000 ? "high" : "normal";
            return task;
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var atLimit = await controller.ExportPlanningReport("html", null, "all", null, null, null, null, priority: "high");
        var atLimitText = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(atLimit).FileContents);
        Assert.Contains("Задач: 5000.", atLimitText);
        Assert.Equal(5000, System.Text.RegularExpressions.Regex.Matches(atLimitText, @"Граничная задача \d+").Count);
        var rendersBeforeLimit = pdf.Calls;
        var rejected = await controller.ExportPlanningReport("pdf", null, "all", null, null, null, null);
        Assert.IsType<BadRequestObjectResult>(rejected);
        Assert.Equal(rendersBeforeLimit, pdf.Calls);
        var narrowed = await controller.ExportPlanningReport("html", null, "all", null, null, null, null, underControl: true);
        var narrowedText = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(narrowed).FileContents);
        Assert.Contains(first.Title, narrowedText);
        Assert.DoesNotContain("Граничная задача", narrowedText);
        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == user.RoleId && item.Section == RolePermissionCatalog.Tasks);
        permission.CanExport = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        foreach (var format in new[] { "html", "word", "pdf" })
        {
            var denied = await controller.ExportPlanningReport(format, null, "all", null, null, null, null);
            Assert.IsType<ForbidResult>(denied);
        }
    }

    private static object?[] Arguments(MethodInfo method, Dictionary<string, object?> values)
        => method.GetParameters().Select(parameter => values.TryGetValue(parameter.Name!, out var value) ? value : parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
    private static string ReadWord(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
    private sealed class CapturePdf : IInvoiceDocumentPdfService
    {
        public int Calls { get; private set; }
        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Encoding.UTF8.GetBytes(html));
        }
    }
    public class NoNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("No notification transport is used by export.");
    }
}
