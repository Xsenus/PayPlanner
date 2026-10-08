using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskBillingConcurrencyTests
{
    [Theory]
    [InlineData("link", false)]
    [InlineData("link", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    public async Task MovingTaskAndBillingItCannotCommitIncompatibleBindings(string billingOperation, bool billingFirst)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (fixture is null) return;
        var firstOperation = billingFirst ? billingOperation : "move";
        var secondOperation = billingFirst ? "move" : billingOperation;
        var (first, second) = await fixture.RaceAsync(firstOperation, secondOperation);
        Assert.IsAssignableFrom<ObjectResult>(first);
        Assert.True(first is OkObjectResult or CreatedResult, $"First operation returned {first.GetType().Name}.");

        await using var db = fixture.Open();
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(billingFirst ? fixture.OriginalClientId : fixture.OtherClientId, task.ClientId);
        Assert.Equal(billingFirst ? fixture.OriginalCaseId : fixture.OtherCaseId, task.ClientCaseId);
        if (billingFirst)
        {
            Assert.NotNull(task.BillingInvoiceId);
            var invoice = await db.Payments.SingleAsync(item => item.Id == task.BillingInvoiceId);
            Assert.Equal(task.ClientId, invoice.ClientId);
            Assert.Equal(task.ClientCaseId, invoice.ClientCaseId);
        }
        else Assert.Null(task.BillingInvoiceId);
        Assert.IsType<BadRequestObjectResult>(second);
    }

    [Theory]
    [InlineData("link", "create")]
    [InlineData("create", "link")]
    public async Task ConcurrentInvoiceCreationAndLinkingCannotBillTheSameTaskTwice(string firstOperation, string secondOperation)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (fixture is null) return;
        var (first, second) = await fixture.RaceAsync(firstOperation, secondOperation);
        Assert.True(first is OkObjectResult or CreatedResult);
        Assert.IsType<BadRequestObjectResult>(second);
        await using var db = fixture.Open();
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.NotNull(task.BillingInvoiceId);
        Assert.Equal(firstOperation == "link", task.BillingInvoiceId == fixture.InvoiceId);
        Assert.Equal(firstOperation == "create" ? 2 : 1, await db.Payments.CountAsync());
    }

    [Theory]
    [InlineData("deleteInvoice", true)]
    [InlineData("moveInvoice", true)]
    [InlineData("deleteInvoice", false)]
    [InlineData("moveInvoice", false)]
    public async Task LinkingTaskPreventsConcurrentInvoiceDeletionOrCounterpartyChange(string operation, bool hasInvoiceEntity = true)
    {
        await using var fixture = await Fixture.CreateAsync(hasInvoiceEntity);
        if (fixture is null) return;
        var (linked, invoiceChange) = await fixture.RaceAsync("link", operation);
        Assert.IsType<OkObjectResult>(linked);
        Assert.IsType<BadRequestObjectResult>(invoiceChange);
        await using var db = fixture.Open();
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        var invoice = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == fixture.InvoiceId);
        Assert.Equal(invoice.Id, task.BillingInvoiceId);
        Assert.Equal(task.ClientId, invoice.ClientId);
        Assert.Equal(task.ClientCaseId, invoice.ClientCaseId);
    }

    private sealed class Fixture(string connectionString, string adminConnectionString, string databaseName) : IAsyncDisposable
    {
        public int UserId { get; private set; }
        public int TaskId { get; private set; }
        public int InvoiceId { get; private set; }
        public int OriginalClientId { get; private set; }
        public int OriginalCaseId { get; private set; }
        public int OtherClientId { get; private set; }
        public int OtherCaseId { get; private set; }

        public PostgreSqlPaymentContext Open(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new PostgreSqlPaymentContext(options.Options);
        }

        public static async Task<Fixture?> CreateAsync(bool hasInvoiceEntity = true)
        {
            var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(configured))
            {
                Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                    "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
                return null;
            }

            var name = "payplanner_task_billing_" + Guid.NewGuid().ToString("N");
            var admin = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                await create.ExecuteNonQueryAsync();
            }
            var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name };
            var fixture = new Fixture(target.ConnectionString, admin.ConnectionString, name);
            try
            {
                await using var db = fixture.Open();
                await db.Database.EnsureCreatedAsync();
                var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "admin") ?? new Role { Name = "admin" };
                var actor = new User { Role = role, Email = "billing-race@payplanner.local", FullName = "Synthetic billing actor", PasswordHash = "unused", IsActive = true, IsApproved = true };
                var original = new Client { Name = "Original synthetic client" };
                var other = new Client { Name = "Other synthetic client" };
                var originalCase = new ClientCase { Client = original, Title = "Original synthetic case" };
                var otherCase = new ClientCase { Client = other, Title = "Other synthetic case" };
                var invoice = new Payment
                {
                    Invoice = hasInvoiceEntity ? new Invoice { Number = "RACE-EXISTING", Date = DateTime.UtcNow.Date } : null,
                    Account = "RACE-EXISTING",
                    DocumentKind = DocumentKind.Invoice,
                    Type = PaymentType.Income,
                    Client = original,
                    ClientCase = originalCase,
                    Amount = 100m,
                    Date = DateTime.UtcNow.Date,
                };
                var task = new TaskJournalItem
                {
                    Client = original,
                    ClientCase = originalCase,
                    AuthorUser = actor,
                    AssigneeUser = actor,
                    Title = "Synthetic billing task",
                    ObjectName = original.Name,
                    DueAt = DateTime.UtcNow.AddDays(1),
                    ClientBillingClassification = TaskClientBillingClassification.Billable,
                };
                db.AddRange(actor, original, other, originalCase, otherCase, invoice, task);
                await db.SaveChangesAsync();
                fixture.UserId = actor.Id;
                fixture.TaskId = task.Id;
                fixture.InvoiceId = invoice.Id;
                fixture.OriginalClientId = original.Id;
                fixture.OtherClientId = other.Id;
                fixture.OriginalCaseId = originalCase.Id;
                fixture.OtherCaseId = otherCase.Id;
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<(IActionResult First, IActionResult Second)> RaceAsync(string firstOperation, string secondOperation)
        {
            var barrier = new BeforeSaveBarrier();
            await using var firstDb = Open(barrier);
            await using var secondDb = Open();
            await firstDb.Database.OpenConnectionAsync();
            await secondDb.Database.OpenConnectionAsync();
            var secondPid = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
            var first = ExecuteAsync(firstDb, firstOperation);
            Task<IActionResult>? second = null;
            try
            {
                await Task.WhenAny(barrier.Arrived.Task, first).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(first.IsCompleted, "First operation must reach the save barrier before returning.");
                second = ExecuteAsync(secondDb, secondOperation);
                // A fixed implementation blocks on the first row lock; the old implementation
                // completes the competing write. Wait for either observable state, not a timing guess.
                await WaitForCompletionOrDatabaseLockAsync(second, secondPid);
            }
            finally { barrier.Release.TrySetResult(); }
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(second);
            return (firstResult, await second.WaitAsync(TimeSpan.FromSeconds(20)));
        }

        private async Task WaitForCompletionOrDatabaseLockAsync(Task operation, int pid)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync(timeout.Token);
            while (!operation.IsCompleted)
            {
                await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", observer);
                query.Parameters.AddWithValue("pid", pid);
                if (await query.ExecuteScalarAsync(timeout.Token) is true) return;
                await Task.Delay(10, timeout.Token);
            }
        }

        private async Task<IActionResult> ExecuteAsync(PostgreSqlPaymentContext db, string operation)
        {
            var context = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                        new Claim(ClaimTypes.Role, "admin"), new Claim(ClaimTypes.Name, "Synthetic billing actor"),
                    ], "Synthetic")),
                },
            };
            var tasks = new TasksController(db, DispatchProxy.Create<INotificationService, NoOutboundNotifications>()) { ControllerContext = context };
            var invoices = new InvoicesController(db, null!, new NoRequiredFields()) { ControllerContext = context };
            return operation switch
            {
                "move" => (await tasks.UpdateTask(TaskId, new UpsertTaskJournalItemRequest
                {
                    ClientId = OtherClientId,
                    ClientCaseId = OtherCaseId,
                    Title = "Moved synthetic task",
                    AssigneeUserId = UserId,
                    DueAt = DateTime.UtcNow.AddDays(1),
                    Status = "new",
                }, CancellationToken.None)).Result!,
                "link" => (await tasks.LinkTaskBillingInvoice(TaskId, new LinkTaskBillingInvoiceRequest
                { InvoiceId = InvoiceId, ConfirmedIncludedInInvoice = true }, CancellationToken.None)).Result!,
                "create" => (await invoices.Create(InvoiceRequest(false, true), CancellationToken.None)).Result!,
                "moveInvoice" => (await invoices.Update(InvoiceId, InvoiceRequest(true, false), CancellationToken.None)).Result!,
                "deleteInvoice" => await invoices.Delete(InvoiceId, CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }

        private UpsertInvoiceRequest InvoiceRequest(bool otherClient, bool newInvoice) => new()
        {
            Number = newInvoice ? "RACE-CREATED" : "RACE-EXISTING",
            Date = DateTime.UtcNow.Date,
            ClientId = otherClient ? OtherClientId : OriginalClientId,
            ClientCaseId = otherClient ? OtherCaseId : OriginalCaseId,
            Amount = 100m,
            BillingTaskIds = newInvoice ? [TaskId] : [],
        };

        public async ValueTask DisposeAsync()
        {
            Assert.StartsWith("payplanner_task_billing_", databaseName, StringComparison.Ordinal);
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class BeforeSaveBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Arrived.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    public class NoOutboundNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod?.ReturnType == typeof(Task<IReadOnlyList<NotificationDelivery>>))
                return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
            throw new InvalidOperationException("Unexpected notification contract in synthetic test.");
        }
    }

    private sealed class NoRequiredFields : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
