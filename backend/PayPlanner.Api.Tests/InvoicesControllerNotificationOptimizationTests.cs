using System.Data.Common;
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
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicesControllerNotificationOptimizationTests
{
    [Fact]
    public async Task InvoiceNotifications_LoadEligibleRecipientsOnceAndDelegateVisibilityCheck()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var actor = await CreateUserAsync(db, "invoice-notification-actor@example.com");
        var responsible = await CreateUserAsync(db, "invoice-notification-recipient@example.com");
        var invoice = new Invoice
        {
            Number = "INV-NOTIFICATION-QUERY",
            Date = new DateTime(2026, 7, 20),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Date = invoice.Date,
            Amount = 25_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            Responsibles =
            {
                new PaymentResponsible { UserId = responsible.Id },
            },
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        payment = await db.Payments.SingleAsync(item => item.Id == payment.Id);

        var notificationService = DispatchProxy.Create<INotificationService, RecordingNotificationServiceProxy>();
        var recorder = (RecordingNotificationServiceProxy)(object)notificationService;
        var controller = new InvoicesController(
            db,
            new NoopEmailService(),
            new NoopFormRequirementsService(),
            notificationService: notificationService)
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
        interceptor.ReaderCommands.Clear();

        await InvokeNotificationMethodAsync(controller, "NotifyInvoiceCreatedAsync", payment, CancellationToken.None);
        await InvokeNotificationMethodAsync(controller, "NotifyInvoiceChangedAsync", payment, new[] { "Статус" }, CancellationToken.None);

        Assert.Equal(2, interceptor.ReaderCommands.Count(command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("RolePermissions", StringComparison.Ordinal)));
        Assert.DoesNotContain(interceptor.ReaderCommands, command =>
            command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Payments\"", StringComparison.Ordinal));
        Assert.Equal(2, recorder.Calls.Count);
        Assert.All(recorder.Calls, call => Assert.Equal(new[] { responsible.Id }, call.RecipientUserIds));
        Assert.Equal(["NotifyInvoiceCreatedAsync", "NotifyInvoiceChangedAsync"], recorder.Calls.Select(call => call.MethodName));
    }

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor interceptor)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string email)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "invoice-notification-test");
        if (role is null)
        {
            role = new Role
            {
                Name = "invoice-notification-test",
                Description = "Invoice notification test",
                CreatedAt = DateTime.UtcNow,
                Permissions =
                {
                    new RolePermission
                    {
                        Section = RolePermissionCatalog.Accounts,
                        CanView = true,
                        ViewScope = DataVisibilityScope.All,
                    },
                },
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task InvokeNotificationMethodAsync(
        InvoicesController controller,
        string methodName,
        params object[] arguments)
    {
        var method = typeof(InvoicesController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(controller, arguments));
        await task;
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
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
        public List<string> ReaderCommands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
