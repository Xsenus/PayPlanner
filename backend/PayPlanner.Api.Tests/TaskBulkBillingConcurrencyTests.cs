using System.Data.Common;
using System.Globalization;
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
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskBulkBillingConcurrencyTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData("link", false)]
    [InlineData("link", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    public async Task BulkAndInvoiceCreationOrLinkingSerializeAndRecheckRawInvoice(string billingOperation, bool billingFirst)
    {
        await using var fixture = await CreateAsync();
        if (fixture is null) return;
        var firstName = billingFirst ? billingOperation : "bulk";
        var secondName = billingFirst ? "bulk" : billingOperation;
        var (first, second) = await fixture.RaceAsync(firstName, secondName);
        Assert.True(first is OkObjectResult or CreatedResult);
        await using var db = fixture.Open();
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        if (billingFirst)
        {
            Assert.NotNull(task.BillingInvoiceId);
            Assert.Equal(TaskClientBillingClassification.Billable, task.ClientBillingClassification);
            var result = Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(second).Value);
            Assert.Equal("invoiced", Assert.Single(result.Items).Reason);
            Assert.Equal(0, result.Updated);
        }
        else
        {
            Assert.Null(task.BillingInvoiceId);
            Assert.Equal(TaskClientBillingClassification.Internal, task.ClientBillingClassification);
            Assert.IsType<BadRequestObjectResult>(second);
        }
        Assert.Equal(billingFirst && billingOperation == "create" ? 2 : 1, await db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkAndSingleClassificationSerializeWithoutLosingAConcurrentDecision(bool singleFirst)
    {
        await using var fixture = await CreateAsync(TaskClientBillingClassification.Legacy);
        if (fixture is null) return;
        var (first, second) = await fixture.RaceAsync(singleFirst ? "single" : "bulk", singleFirst ? "bulk" : "single");
        Assert.IsType<OkObjectResult>(first);
        await using var db = fixture.Open();
        Assert.Equal(TaskClientBillingClassification.Billable, (await db.TaskJournalItems.AsNoTracking().SingleAsync()).ClientBillingClassification);
        if (singleFirst)
        {
            var result = Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(second).Value);
            Assert.Equal("versionConflict", Assert.Single(result.Items).Reason);
            Assert.Equal(1, await db.TaskJournalComments.CountAsync());
        }
        else
        {
            Assert.IsType<OkObjectResult>(second);
            Assert.Equal(2, await db.TaskJournalComments.CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkAndTaskEditSerializeWithoutOverwritingResultOrNewerVersion(bool editFirst)
    {
        await using var fixture = await CreateAsync();
        if (fixture is null) return;
        var (_, second) = await fixture.RaceAsync(editFirst ? "edit" : "bulk", editFirst ? "bulk" : "edit");
        await using var db = fixture.Open();
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal("Edited synthetic task", task.Title);
        Assert.Equal("Preserved result", task.Result);
        Assert.Equal(editFirst ? TaskClientBillingClassification.Billable : TaskClientBillingClassification.Internal, task.ClientBillingClassification);
        if (editFirst)
        {
            var result = Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(second).Value);
            Assert.Equal(1, result.Conflicted);
        }
        else Assert.IsType<OkObjectResult>(second);
    }

    [Fact]
    public async Task OppositeIdOrdersTakeOrderedLocksAndReturnPersistedMicrosecondTokens()
    {
        await using var fixture = await CreateAsync(TaskClientBillingClassification.Legacy, taskCount: 2);
        if (fixture is null) return;
        var (first, second) = await fixture.RaceAsync("bulk", "bulkReverse");
        var firstResult = Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(first).Value);
        var secondResult = Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(second).Value);
        Assert.Equal(2, firstResult.Updated);
        Assert.Equal(2, secondResult.Conflicted);
        await using var db = fixture.Open();
        var stored = await db.TaskJournalItems.AsNoTracking().OrderBy(task => task.Id).ToArrayAsync();
        foreach (var row in stored)
        {
            Assert.NotNull(row.UpdatedAt);
            Assert.Equal(0, row.UpdatedAt.Value.Ticks % 10);
            Assert.Equal(Token(row.UpdatedAt), firstResult.Items.Single(item => item.Id == row.Id).UpdatedAt);
        }
        var acknowledgedAgain = await fixture.Tasks(db).BulkUpdateTasks(fixture.Request(), CancellationToken.None);
        Assert.Equal(2, Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(acknowledgedAgain.Result).Value).Unchanged);
        Assert.Equal(2, await db.TaskJournalComments.CountAsync());
        // The exact persisted result is usable as the next optimistic precondition.
        var next = fixture.Request(TaskClientBillingClassification.Billable);
        next.ExpectedUpdatedAtById = firstResult.Items.ToDictionary(item => item.Id, item => item.UpdatedAt);
        var retry = await fixture.Tasks(db).BulkUpdateTasks(next, CancellationToken.None);
        Assert.Equal(2, Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(retry.Result).Value).Updated);
    }

    [Fact]
    public async Task PermissionRevokedAfterTaskLockIsReadAgainBeforeMutation()
    {
        await using var fixture = await CreateAsync(restricted: true);
        if (fixture is null) return;
        var revoke = new AfterTaskLock(async () =>
        {
            await using var authority = fixture.Open();
            var permission = await authority.RolePermissions.SingleAsync(row => row.Id == fixture.PermissionId);
            permission.CanEdit = false;
            permission.CanEditOthers = false;
            await authority.SaveChangesAsync();
        });
        await using var db = fixture.Open(revoke);
        var before = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        var response = await fixture.Tasks(db).BulkUpdateTasks(fixture.Request(), CancellationToken.None);
        Assert.True(revoke.Invoked);
        Assert.IsType<ForbidResult>(response.Result);
        var after = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(before.ClientBillingClassification, after.ClientBillingClassification);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(await db.TaskJournalComments.ToArrayAsync());
    }

    [Fact]
    public async Task PostgreSqlDoesNotRoundAnUnmatchedExpectedVersionIntoAMatch()
    {
        await using var fixture = await CreateAsync();
        if (fixture is null) return;
        await using var db = fixture.Open();
        var request = fixture.Request();
        var taskId = Assert.Single(fixture.TaskIds);
        Assert.NotNull(fixture.Versions[taskId]);
        request.ExpectedUpdatedAtById![taskId] = Token(fixture.Versions[taskId]!.Value.AddTicks(1));
        var response = await fixture.Tasks(db).BulkUpdateTasks(request, CancellationToken.None);
        Assert.Equal("versionConflict", Assert.Single(Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(response.Result).Value).Items).Reason);
        Assert.Empty(await db.TaskJournalComments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlRecognizesPaidPayoutThroughRawLinkOrExistingMarker(bool markerOnly)
    {
        await using var fixture = await CreateAsync();
        if (fixture is null) return;
        await using var db = fixture.Open();
        var task = await db.TaskJournalItems.SingleAsync();
        var payment = new Payment { Amount = 100m, PaidAmount = 1m, Date = DateTime.UtcNow.Date, SystemNotes = $"[PayPlanner:TaskContractorPayment]:{task.Id}\nSynthetic payment" };
        db.Payments.Add(payment);
        if (!markerOnly) task.ContractorPayment = payment;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        var response = await fixture.Tasks(db).BulkUpdateTasks(fixture.Request(), CancellationToken.None);
        Assert.Equal("paid", Assert.Single(Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>(response.Result).Value).Items).Reason);
        var after = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(before.ClientBillingClassification, after.ClientBillingClassification);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(await db.TaskJournalComments.ToArrayAsync());
    }

    private async Task<Fixture?> CreateAsync(string classification = TaskClientBillingClassification.Billable, int taskCount = 1, bool restricted = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase), "PostgreSQL concurrency checks require PAYPLANNER_TEST_POSTGRES in CI.");
            return null;
        }
        var lease = await template.CreateDatabaseAsync("pp_task_bulk_billing_");
        var fixture = new Fixture(lease);
        try
        {
            await using var db = fixture.Open();
            var role = restricted ? new Role { Name = "synthetic-bulk-editor" }
                : await db.Roles.FirstOrDefaultAsync(role => role.Name == "admin") ?? new Role { Name = "admin" };
            var permission = new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, CanEdit = true, CanEditOthers = true, ViewScope = DataVisibilityScope.All };
            if (restricted) role.Permissions.Add(permission);
            var user = new User { Role = role, Email = "bulk-race@example.test", FullName = "Synthetic billing actor", PasswordHash = "unused", IsActive = true, IsApproved = true };
            var client = new Client { Name = "Synthetic client" };
            var clientCase = new ClientCase { Client = client, Title = "Synthetic case" };
            var invoice = new Payment { Invoice = new Invoice { Number = "SYNTHETIC-RACE", Date = DateTime.UtcNow.Date }, Account = "SYNTHETIC-RACE", DocumentKind = DocumentKind.Invoice, Type = PaymentType.Income, Client = client, ClientCase = clientCase, Amount = 100m, Date = DateTime.UtcNow.Date };
            var tasks = Enumerable.Range(0, taskCount).Select(_ => new TaskJournalItem
            {
                AuthorUser = user,
                AssigneeUser = user,
                Client = client,
                ClientCase = clientCase,
                Title = "Synthetic task",
                ObjectName = client.Name,
                DueAt = DateTime.UtcNow.AddDays(1),
                Status = "new",
                Result = "Preserved result",
                ClientBillingClassification = classification,
                UpdatedAt = DateTime.UtcNow.AddDays(-1),
            }).ToArray();
            db.AddRange(user, client, clientCase, invoice);
            db.TaskJournalItems.AddRange(tasks);
            await db.SaveChangesAsync();
            fixture.UserId = user.Id;
            fixture.PermissionId = permission.Id;
            fixture.ClientId = client.Id;
            fixture.CaseId = clientCase.Id;
            fixture.InvoiceId = invoice.Id;
            fixture.TaskIds = tasks.Select(task => task.Id).OrderBy(id => id).ToArray();
            fixture.Versions = await db.TaskJournalItems.AsNoTracking().ToDictionaryAsync(task => task.Id, task => task.UpdatedAt);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static string? Token(DateTime? value) => value.HasValue
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : null;

    private sealed class Fixture(PostgreSqlMigratedTemplate.DatabaseLease lease) : IAsyncDisposable
    {
        public int UserId { get; set; }
        public int ClientId { get; set; }
        public int CaseId { get; set; }
        public int InvoiceId { get; set; }
        public int PermissionId { get; set; }
        public int[] TaskIds { get; set; } = [];
        public Dictionary<int, DateTime?> Versions { get; set; } = [];
        public PostgreSqlPaymentContext Open(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new PostgreSqlPaymentContext(options.Options);
        }

        public BulkUpdateTaskJournalItemsRequest Request(string target = TaskClientBillingClassification.Internal, bool reverse = false) => new()
        {
            Ids = (reverse ? TaskIds.Reverse() : TaskIds).ToList(),
            BillingClassification = target,
            ExpectedUpdatedAtById = Versions.ToDictionary(pair => pair.Key, pair => Token(pair.Value)),
        };

        private ControllerContext Context() => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Name, "Synthetic billing actor"),
            ], "Synthetic"))
            },
        };

        public TasksController Tasks(PostgreSqlPaymentContext db) => new(db, DispatchProxy.Create<INotificationService, NoOutboundNotifications>()) { ControllerContext = Context() };

        private async Task<IActionResult> ExecuteAsync(PostgreSqlPaymentContext db, string name)
        {
            var controller = Tasks(db);
            return name switch
            {
                "bulk" => (await controller.BulkUpdateTasks(Request(), CancellationToken.None)).Result!,
                "bulkReverse" => (await controller.BulkUpdateTasks(Request(TaskClientBillingClassification.Billable, reverse: true), CancellationToken.None)).Result!,
                "single" => (await controller.UpdateTaskBillingClassification(TaskIds[0], new UpdateTaskBillingClassificationRequest { Classification = TaskClientBillingClassification.Billable }, CancellationToken.None)).Result!,
                "link" => (await controller.LinkTaskBillingInvoice(TaskIds[0], new LinkTaskBillingInvoiceRequest { InvoiceId = InvoiceId, ConfirmedIncludedInInvoice = true }, CancellationToken.None)).Result!,
                "create" => (await new InvoicesController(db, null!, new NoRequiredFields()) { ControllerContext = Context() }.Create(new UpsertInvoiceRequest
                {
                    Number = "SYNTHETIC-CREATED",
                    Date = DateTime.UtcNow.Date,
                    ClientId = ClientId,
                    ClientCaseId = CaseId,
                    Amount = 100m,
                    BillingTaskIds = [TaskIds[0]],
                }, CancellationToken.None)).Result!,
                "edit" => (await controller.UpdateTask(TaskIds[0], new UpsertTaskJournalItemRequest
                {
                    ClientId = ClientId,
                    ClientCaseId = CaseId,
                    AssigneeUserId = UserId,
                    Title = "Edited synthetic task",
                    ObjectName = "Synthetic client",
                    DueAt = DateTime.UtcNow.AddDays(1),
                    Status = "new",
                    Result = "Preserved result",
                }, CancellationToken.None)).Result!,
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
        }

        public async Task<(IActionResult First, IActionResult Second)> RaceAsync(string firstName, string secondName)
        {
            var barrier = new BeforeSaveBarrier();
            await using var firstDb = Open(barrier);
            await using var secondDb = Open();
            await firstDb.Database.OpenConnectionAsync();
            await secondDb.Database.OpenConnectionAsync();
            var secondPid = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
            var first = ExecuteAsync(firstDb, firstName);
            Task<IActionResult>? second = null;
            try
            {
                await Task.WhenAny(first, barrier.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(first.IsCompleted, "First operation must reach save while holding its task locks.");
                second = ExecuteAsync(secondDb, secondName);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var observer = new NpgsqlConnection(lease.ConnectionString);
                await observer.OpenAsync(timeout.Token);
                while (!second.IsCompleted)
                {
                    await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", observer);
                    query.Parameters.AddWithValue("pid", secondPid);
                    if (await query.ExecuteScalarAsync(timeout.Token) is true) break;
                    await Task.Delay(10, timeout.Token);
                }
            }
            finally { barrier.Release.TrySetResult(); }
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(second);
            return (firstResult, await second.WaitAsync(TimeSpan.FromSeconds(20)));
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private sealed class BeforeSaveBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Arrived.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private sealed class AfterTaskLock(Func<Task> action) : DbCommandInterceptor
    {
        public bool Invoked { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Invoked && command.CommandText.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Invoked = true;
                await action();
            }
            return result;
        }
    }

    public class NoOutboundNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod?.ReturnType == typeof(Task<IReadOnlyList<NotificationDelivery>>)) return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
            throw new InvalidOperationException("Unexpected notification contract in isolated billing concurrency test.");
        }
    }

    private sealed class NoRequiredFields : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
