using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PaymentsControllerNotificationOptimizationTests
{
    [Fact]
    public async Task PaymentNotifications_LoadEligibleRecipientsOnceAndDelegateVisibilityCheck()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        var (actor, responsible) = await CreateUsersAsync(db);
        var payment = new Payment
        {
            Description = "Payment notification query",
            Date = new DateTime(2026, 7, 20),
            Amount = 25_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            DocumentKind = DocumentKind.Payment,
            Responsibles =
            {
                new PaymentResponsible { UserId = responsible.Id },
            },
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var (controller, recorder) = CreateController(db, actor);
        interceptor.Commands.Clear();

        await InvokeNotificationMethodAsync(controller, "NotifyPaymentAssignedAsync", payment, CancellationToken.None);
        await InvokeNotificationMethodAsync(controller, "NotifyPaymentChangedAsync", payment, new[] { "Статус" }, CancellationToken.None);

        Assert.Equal(2, CountRecipientCandidateQueries(interceptor.Commands));
        Assert.DoesNotContain(interceptor.Commands, IsPerRecipientPaymentVisibilityQuery);
        Assert.Equal(["NotifyPaymentAssignedAsync", "NotifyPaymentChangedAsync"], recorder.Calls.Select(call => call.MethodName));
        Assert.All(recorder.Calls, call => Assert.Equal(new[] { responsible.Id }, call.RecipientUserIds));
    }

    [Fact]
    public async Task InvoiceStateNotification_LoadsEligibleRecipientsOnceAndDelegatesVisibilityCheck()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        var (actor, responsible) = await CreateUsersAsync(db);
        var invoice = new Invoice
        {
            Number = "INV-NOTIFICATION-QUERY",
            Date = new DateTime(2026, 7, 20),
        };
        var payment = new Payment
        {
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Date = invoice.Date,
            Amount = 25_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            DocumentKind = DocumentKind.Invoice,
            Invoice = invoice,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Responsibles =
            {
                new PaymentResponsible { UserId = responsible.Id },
            },
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var (controller, recorder) = CreateController(db, actor);
        var snapshot = await InvokeResultMethodAsync(
            controller,
            "CaptureInvoiceNotificationSnapshotAsync",
            payment,
            CancellationToken.None);
        payment.Status = PaymentStatus.Completed;
        payment.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Paid;
        payment.PaidAmount = payment.Amount;
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();

        await InvokeNotificationMethodAsync(
            controller,
            "NotifyInvoiceStateChangedAsync",
            payment,
            snapshot,
            CancellationToken.None);

        Assert.Equal(1, CountRecipientCandidateQueries(interceptor.Commands));
        Assert.DoesNotContain(interceptor.Commands, IsPerRecipientPaymentVisibilityQuery);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal("NotifyInvoiceChangedAsync", call.MethodName);
        Assert.Equal(new[] { responsible.Id }, call.RecipientUserIds);
    }

    private static (PaymentsController Controller, RecordingNotificationServiceProxy Recorder) CreateController(
        PaymentContext db,
        User actor)
    {
        var notificationService = DispatchProxy.Create<INotificationService, RecordingNotificationServiceProxy>();
        var recorder = (RecordingNotificationServiceProxy)(object)notificationService;
        var controller = new PaymentsController(
            db,
            new NoopFormRequirementsService(),
            notificationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()),
                        new Claim(ClaimTypes.Name, actor.FullName),
                    ], "Test")),
                },
            },
        };

        return (controller, recorder);
    }

    private static async Task<(User Actor, User Responsible)> CreateUsersAsync(PaymentContext db)
    {
        var role = new Role
        {
            Name = $"payment-notification-optimization-{Guid.NewGuid():N}",
            Description = "Payment notification optimization test",
            Permissions =
            {
                new RolePermission
                {
                    Section = RolePermissionCatalog.Payments,
                    CanView = true,
                    ViewScope = DataVisibilityScope.Own,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Accounts,
                    CanView = true,
                    ViewScope = DataVisibilityScope.Own,
                },
            },
        };
        var actor = CreateUser(role, "payment-notification-actor@example.test");
        var responsible = CreateUser(role, "payment-notification-recipient@example.test");
        db.Users.AddRange(actor, responsible);
        await db.SaveChangesAsync();
        return (actor, responsible);
    }

    private static User CreateUser(Role role, string email)
        => new()
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static int CountRecipientCandidateQueries(IEnumerable<string> commands)
        => commands.Count(command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("RolePermissions", StringComparison.Ordinal));

    private static bool IsPerRecipientPaymentVisibilityQuery(string command)
        => command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Payments\"", StringComparison.Ordinal);

    private static async Task InvokeNotificationMethodAsync(
        PaymentsController controller,
        string methodName,
        params object[] arguments)
    {
        var method = typeof(PaymentsController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(controller, arguments));
        await task;
    }

    private static async Task<object> InvokeResultMethodAsync(
        PaymentsController controller,
        string methodName,
        params object[] arguments)
    {
        var method = typeof(PaymentsController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(controller, arguments));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);
        return result;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    private class RecordingNotificationServiceProxy : DispatchProxy
    {
        public List<(string MethodName, IReadOnlyCollection<int> RecipientUserIds)> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(targetMethod);
            Assert.NotNull(args);
            var recipientUserIds = Assert.IsAssignableFrom<IReadOnlyCollection<int>>(args[^2]);
            Calls.Add((targetMethod.Name, recipientUserIds.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
