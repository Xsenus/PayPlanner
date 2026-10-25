using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;
using SourceFixture = PayPlanner.Api.Tests.ActsSourceAvailabilityTests.Fixture;

namespace PayPlanner.Api.Tests;

public sealed class ActsSourceAvailabilityConcurrencyTests(PostgreSqlMigratedTemplate template)
    : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false, "Payments")]
    [InlineData(true, "Payments")]
    [InlineData(false, "Contracts")]
    [InlineData(true, "Contracts")]
    [InlineData(false, "Clients")]
    [InlineData(true, "Clients")]
    [InlineData(false, "LegalEntities")]
    [InlineData(true, "LegalEntities")]
    [InlineData(true, "Acts")]
    public async Task Save_BusySourceRootReturnsConflictAndLeavesGraphUnchanged(bool update, string table)
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        if (table == "LegalEntities")
        {
            f.Source.Invoice.ClientId = null;
            f.Source.Invoice.LegalEntityId = f.Source.ForeignCompany.Id;
            await f.Source.Db.SaveChangesAsync();
        }
        var request = f.Request();
        if (table == "Contracts") request.ContractId = f.Source.Contract.Id;
        var rowId = table switch
        {
            "Payments" => f.Source.Invoice.Id,
            "Contracts" => f.Source.Contract.Id,
            "Clients" => f.Source.ForeignClient.Id,
            "LegalEntities" => f.Source.ForeignCompany.Id,
            "Acts" => f.Source.Existing.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        var before = await f.Source.SnapshotAsync();
        await using var blocker = f.Open();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await using var command = blocker.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        // Identifiers are a fixed test-only allowlist above; the row ID remains a parameter.
        command.CommandText = $"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\" = @id FOR UPDATE";
        var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = rowId;
        command.Parameters.Add(parameter);
        Assert.NotNull(await command.ExecuteScalarAsync());

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = update
            ? await f.Source.Controller.Update(f.Source.Existing.Id, request, deadline.Token)
            : await f.Source.Controller.Create(request, deadline.Token);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(f.Source.Notifications.Calls);
        Assert.Equal(before, await f.Source.SnapshotAsync());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_RechecksSourcePermissionAfterLeaseAdmission(bool update)
    {
        string? expected = null;
        PgFixture? fixture = null;
        var revoke = new BeforeFirstPaymentLease(async () =>
        {
            await using var authority = fixture!.Open();
            var permission = await authority.RolePermissions.SingleAsync(p => p.RoleId == fixture.Source.Actor.RoleId
                && p.Section == RolePermissionCatalog.Accounts);
            permission.CanView = false;
            await authority.SaveChangesAsync();
            expected = await SourceFixture.CaptureAsync(authority);
        });
        await using var f = await CreateAsync(revoke);
        if (f is null) return;
        fixture = f;
        var request = f.Request();

        var result = await f.Source.SaveAsync(update, request);

        Assert.True(revoke.Triggered);
        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(expected, await f.Source.SnapshotAsync());
        Assert.Empty(f.Source.Notifications.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_RechecksActualInvoiceOwnerInsteadOfPreflightMetadata(bool update)
    {
        string? expected = null;
        PgFixture? fixture = null;
        var changeOwner = new BeforeFirstPaymentLease(async () =>
        {
            await using var authority = fixture!.Open();
            var invoice = await authority.Payments.SingleAsync(p => p.Id == fixture.Source.Invoice.Id);
            invoice.ClientId = fixture.Source.OwnClient.Id;
            await authority.SaveChangesAsync();
            expected = await SourceFixture.CaptureAsync(authority);
        });
        await using var f = await CreateAsync(changeOwner);
        if (f is null) return;
        fixture = f;
        var request = f.Request();
        request.ClientId = f.Source.ForeignClient.Id;

        var result = await f.Source.SaveAsync(update, request);

        Assert.True(changeOwner.Triggered);
        if (update) Assert.IsType<NotFoundObjectResult>(result.Result);
        else Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(expected, await f.Source.SnapshotAsync());
        Assert.Empty(f.Source.Notifications.Calls);
        if (!update)
        {
            // An explicit later request must use the current owner, with no automatic retry.
            var freshRequest = await f.Source.SaveAsync(false, request);
            Assert.IsType<NotFoundObjectResult>(freshRequest.Result);
            Assert.Equal(expected, await f.Source.SnapshotAsync());
            Assert.Empty(f.Source.Notifications.Calls);
        }
    }

    [Theory]
    [InlineData("update", false)]
    [InlineData("cancel", false)]
    [InlineData("update", true)]
    [InlineData("cancel", true)]
    public async Task ContractWriterAndNewActParticipateBeforeAuthoritativeRelationRead(string writer, bool actFirst)
    {
        var barrier = new AfterContractLease();
        await using var f = await CreateAsync(actFirst ? barrier : null);
        if (f is null) return;
        f.Source.Invoice.ContractId = f.Source.Contract.Id;
        f.Source.Db.ContractInvoices.Add(new ContractInvoice { ContractId = f.Source.Contract.Id, InvoiceId = f.Source.Invoice.Id });
        await f.Source.Db.SaveChangesAsync();
        await using var writerDb = f.Open(actFirst ? null : barrier);
        var controller = new ContractsController(writerDb, new NoopRequirements())
        {
            ControllerContext = f.Source.Controller.ControllerContext,
        };
        var request = f.Request();
        request.ContractId = f.Source.Contract.Id;

        async Task<ActionResult<ContractDto>> WriteAsync()
            => writer == "cancel"
                ? await controller.Cancel(f.Source.Contract.Id, new CancelContractRequest { Reason = "Synthetic cancellation" }, default)
                : await controller.Update(f.Source.Contract.Id, new UpsertContractRequest
                {
                    Number = "SYN-CONTRACT",
                    Date = new DateTime(2026, 10, 24),
                    Title = "Synthetic updated contract",
                    BillingMode = ContractBillingMode.Actual,
                    IsOpenEnded = true,
                    Clients = [new UpsertContractClientRequest { ClientId = f.Source.ForeignClient.Id }],
                    InvoiceIds = [f.Source.Invoice.Id],
                }, default);

        var first = actFirst ? (Task)f.Source.Controller.Create(request, default) : WriteAsync();
        try
        {
            await Task.WhenAny(first, barrier.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted, "First writer must hold its parent lease before the competing mutation.");
            if (actFirst)
            {
                var competing = await WriteAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsType<ConflictObjectResult>(competing.Result);
            }
            else
            {
                var competing = await f.Source.Controller.Create(request, default).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsType<ConflictObjectResult>(competing.Result);
            }
        }
        finally { barrier.Release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await using var read = f.Open();
        var contract = await read.Contracts.SingleAsync(c => c.Id == f.Source.Contract.Id);
        Assert.Equal(actFirst ? 2 : 1, await read.Acts.CountAsync());
        Assert.Equal(actFirst ? 1 : 0, await read.ContractActs.CountAsync());
        if (actFirst)
        {
            Assert.Null(contract.Title);
            Assert.Equal(ContractStatus.Draft, contract.Status);
        }
        else if (writer == "cancel") Assert.Equal(ContractStatus.Cancelled, contract.Status);
        else Assert.Equal("Synthetic updated contract", contract.Title);
    }

    [Theory]
    [InlineData(ActCreationMode.Full)]
    [InlineData(ActCreationMode.Partial)]
    public async Task ConcurrentCreatesOnSameInvoiceRejectLoserAndNeverOverconsumeRemainingQuantity(string mode)
    {
        var barrier = new AfterContractLease();
        await using var f = await CreateAsync(barrier);
        if (f is null) return;
        var invoiceLine = await f.Source.Db.InvoiceLineItems.AsNoTracking().SingleAsync();
        UpsertActRequest Request()
        {
            var value = f.Request();
            value.ContractId = f.Source.Contract.Id;
            value.CreationMode = mode;
            if (mode == ActCreationMode.Partial)
                value.LineItems = [new ActLineItemRequest { SourceKey = $"db:{invoiceLine.Id}", Quantity = invoiceLine.Quantity }];
            return value;
        }
        async Task<string> SourceFactsAsync(PaymentContext db) => System.Text.Json.JsonSerializer.Serialize(new
        {
            Payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.InvoiceId, p.ClientId, p.LegalEntityId, p.ContractId, p.Amount, p.Date, p.Status, p.PaidAmount, p.IsPaid }).ToArrayAsync(),
            Lines = await db.InvoiceLineItems.AsNoTracking().OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.PaymentId, l.Name, l.Quantity, l.Price }).ToArrayAsync(),
            InvoiceLinks = await db.ContractInvoices.AsNoTracking().OrderBy(l => l.ContractId).ThenBy(l => l.InvoiceId)
                .Select(l => new { l.ContractId, l.InvoiceId }).ToArrayAsync(),
            PaymentLinks = await db.ContractPayments.AsNoTracking().OrderBy(l => l.ContractId).ThenBy(l => l.PaymentId)
                .Select(l => new { l.ContractId, l.PaymentId }).ToArrayAsync(),
        });
        var sourceBefore = await SourceFactsAsync(f.Source.Db);
        var actCountBefore = await f.Source.Db.Acts.CountAsync();
        await using var otherDb = f.Open();
        var notifications = System.Reflection.DispatchProxy.Create<INotificationService, ActsSourceAvailabilityTests.NotificationRecorder>();
        var recorder = (ActsSourceAvailabilityTests.NotificationRecorder)(object)notifications;
        var competingController = new ActsController(otherDb, new NoopRequirements(), notificationService: notifications)
        {
            ControllerContext = f.Source.Controller.ControllerContext,
        };
        var first = f.Source.Controller.Create(Request(), default);
        try
        {
            await Task.WhenAny(first, barrier.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted, "First Create must hold the invoice and contract leases before the competitor.");
            var loser = await competingController.Create(Request(), default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsType<ConflictObjectResult>(loser.Result);
            Assert.Empty(recorder.Calls);
        }
        finally { barrier.Release.TrySetResult(); }
        var createdResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        var created = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>(createdResult.Result).Value);
        var auditAfterCommit = await otherDb.ObjectAuditEvents.CountAsync();

        // This is an explicit second request after a known commit, never an automatic retry.
        var noRemaining = await competingController.Create(Request(), default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(noRemaining.Result).StatusCode);
        Assert.Empty(recorder.Calls);
        await using var read = f.Open();
        Assert.Equal(sourceBefore, await SourceFactsAsync(read));
        Assert.Equal(actCountBefore + 1, await read.Acts.CountAsync());
        var saved = await read.Acts.AsNoTracking().Where(a => a.Id == created.Id).SingleAsync();
        Assert.Equal(f.Source.Invoice.Id, saved.InvoiceId);
        Assert.Equal(123.45m, saved.Amount);
        var stored = await read.Set<ActLineItem>().AsNoTracking().Where(l => l.ActId == created.Id).ToArrayAsync();
        var line = Assert.Single(stored);
        Assert.Equal(invoiceLine.Id, line.InvoiceLineItemId);
        Assert.Equal(invoiceLine.Quantity, line.Quantity);
        Assert.Equal(invoiceLine.Price, line.Price);
        Assert.Equal(auditAfterCommit, await read.ObjectAuditEvents.CountAsync());
        var link = Assert.Single(await read.ContractActs.AsNoTracking().ToArrayAsync());
        Assert.Equal(created.Id, link.ActId);
        Assert.Equal(f.Source.Contract.Id, link.ContractId);
    }

    [Theory]
    [InlineData(false, "Payments")]
    [InlineData(true, "Payments")]
    [InlineData(true, "Acts")]
    public async Task Save_LeasesSoftDeletedRawRootsAndPreventsRestoreBeforeFreshAvailabilityCheck(bool update, string table)
    {
        PgFixture? fixture = null;
        string? deletedSnapshot = null;
        var rowId = 0;
        var deleteAfterPreflight = new AfterActorTransactionStarted(async () =>
        {
            await using var authority = fixture!.Open();
            if (table == "Payments")
                authority.Entry(await authority.Payments.SingleAsync(p => p.Id == rowId)).Property("IsDeleted").CurrentValue = true;
            else authority.Entry(await authority.Acts.SingleAsync(a => a.Id == rowId)).Property("IsDeleted").CurrentValue = true;
            await authority.SaveChangesAsync();
            deletedSnapshot = await SourceFixture.CaptureAsync(authority);
        });
        var restoreBlocked = false;
        var testRestoreAdmission = new AfterRawRootLease(table, async () =>
        {
            await using var authority = fixture!.Open();
            await using var transaction = await authority.Database.BeginTransactionAsync();
            await using var command = authority.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            // A restorer must obtain the deleted row before it can update it. NOWAIT
            // makes this deterministic without sleeps, polling, or a blocked writer task.
            command.CommandText = $"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\" = @id FOR UPDATE NOWAIT";
            var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = rowId;
            command.Parameters.Add(parameter);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            restoreBlocked = true;
            await transaction.RollbackAsync();
        });
        await using var f = await CreateAsync(deleteAfterPreflight, testRestoreAdmission);
        if (f is null) return;
        fixture = f;
        rowId = table == "Payments" ? f.Source.Invoice.Id : f.Source.Existing.Id;
        var request = f.Request();
        deleteAfterPreflight.Armed = true;

        var result = await f.Source.SaveAsync(update, request).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(deleteAfterPreflight.Triggered);
        Assert.True(testRestoreAdmission.Triggered, "The raw deleted source/target must still be included in the lock set.");
        Assert.True(restoreBlocked);
        if (table == "Acts") Assert.IsType<NotFoundResult>(result.Result);
        else Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(deletedSnapshot, await f.Source.SnapshotAsync());
        Assert.Empty(f.Source.Notifications.Calls);
        await using var read = f.Open();
        if (table == "Payments") Assert.True(await read.Payments.IgnoreQueryFilters().Where(p => p.Id == rowId).Select(p => EF.Property<bool>(p, "IsDeleted")).SingleAsync());
        else Assert.True(await read.Acts.IgnoreQueryFilters().Where(a => a.Id == rowId).Select(a => EF.Property<bool>(a, "IsDeleted")).SingleAsync());
        // After the actor has rolled back, the independent restorer can save normally.
        if (table == "Payments") read.Entry(await read.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == rowId)).Property("IsDeleted").CurrentValue = false;
        else read.Entry(await read.Acts.IgnoreQueryFilters().SingleAsync(a => a.Id == rowId)).Property("IsDeleted").CurrentValue = false;
        await read.SaveChangesAsync();
    }

    [Fact]
    public async Task Save_FailureAfterActInsertRollsBackGraphAuditAndVisibility()
    {
        var fault = new AfterActInsertFailure();
        await using var f = await CreateAsync(fault);
        if (f is null) return;
        var request = f.Request();
        request.ContractId = f.Source.Contract.Id;
        var before = await f.Source.SnapshotAsync();
        fault.Armed = true;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => f.Source.Controller.Create(request, default));
        Console.WriteLine(error.ToString());
        Assert.True(fault.Triggered);
        Assert.True(HasSyntheticFailure(error), "The failure must be the injected post-insert error, including an EF wrapper.");
        Assert.Equal(before, await f.Source.SnapshotAsync());
        Assert.Empty(f.Source.Notifications.Calls);
    }

    private static bool HasSyntheticFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SyntheticSaveException) return true;
        return false;
    }

    private async Task<PgFixture?> CreateAsync(params IInterceptor?[] interceptors)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PostgreSQL source-concurrency tests require PAYPLANNER_TEST_POSTGRES in CI.");
            return null;
        }
        var lease = await template.CreateDatabaseAsync("pp_act_source_");
        try
        {
            var source = await SourceFixture.CreatePostgreSqlAsync(lease.ConnectionString, interceptors.OfType<IInterceptor>().ToArray());
            foreach (var section in new[] { RolePermissionCatalog.Accounts, RolePermissionCatalog.Clients,
                         RolePermissionCatalog.LegalEntities, RolePermissionCatalog.Contracts })
                source.Permission(section).ViewScope = DataVisibilityScope.All;
            source.Permission(RolePermissionCatalog.Contracts).CanCreate = true;
            source.Permission(RolePermissionCatalog.Contracts).CanEdit = true;
            await source.Db.SaveChangesAsync();
            return new PgFixture(lease, source);
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private sealed record PgFixture(PostgreSqlMigratedTemplate.DatabaseLease Lease, SourceFixture Source) : IAsyncDisposable
    {
        public PostgreSqlPaymentContext Open(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(Lease.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new PostgreSqlPaymentContext(options.Options);
        }
        public UpsertActRequest Request()
        {
            var request = Source.Request();
            request.InvoiceId = Source.Invoice.Id;
            request.ClientId = null;
            request.CreationMode = ActCreationMode.Full;
            return request;
        }
        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await Lease.DisposeAsync();
        }
    }

    private sealed class AfterActorTransactionStarted(Func<Task> action) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered)
            {
                Triggered = true;
                await action();
            }
            return result;
        }
    }

    private sealed class AfterRawRootLease(string table, Func<Task> action) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                Triggered = true;
                await action();
            }
            return result;
        }
    }

    private sealed class BeforeFirstPaymentLease(Func<Task> action) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("FROM \"Payments\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                Triggered = true;
                await action();
            }
            return result;
        }
    }

    private sealed class AfterContractLease : DbCommandInterceptor
    {
        private bool triggered;
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!triggered && command.CommandText.Contains("FROM \"Contracts\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                triggered = true;
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class AfterActInsertFailure : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"Acts\"", StringComparison.Ordinal))
            {
                Triggered = true;
                await result.DisposeAsync();
                throw new SyntheticSaveException();
            }
            return result;
        }
    }

    private sealed class SyntheticSaveException : Exception { }
    private sealed class NoopRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
