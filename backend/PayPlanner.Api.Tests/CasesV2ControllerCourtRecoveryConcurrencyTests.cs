using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, "update")]
    [InlineData(false, "delete")]
    [InlineData(false, "paid")]
    [InlineData(true, "update")]
    [InlineData(true, "delete")]
    [InlineData(true, "paid")]
    [InlineData(false, "status")]
    [InlineData(false, "association")]
    [InlineData(false, "award")]
    [InlineData(false, "permission")]
    [InlineData(true, "status")]
    [InlineData(true, "association")]
    [InlineData(true, "award")]
    [InlineData(true, "permission")]
    public async Task CourtRecoveries_ConcurrentWriterPreservesOtherCaseAndPaidBoundary(bool postgres, string operation)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var sqlite = $"Data Source=court-recovery-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = postgres ? null : new SqliteConnection(sqlite);
        if (anchor is not null) await anchor.OpenAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(database!.ConnectionString).AddInterceptors(interceptors).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(sqlite).AddInterceptors(interceptors).Options, useObjectAuditModel: true);

        await using var seed = Open();
        if (!postgres) await seed.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(seed, "recovery-race@example.local", "Юрист", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(seed, user, RolePermissionCatalog.Payments);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id };
        var first = new ClientCase { Client = client, ResponsibleUserId = user.Id, Title = "Первое", Status = ClientCaseStatus.Open, CourtAwardedAmount = 1000m };
        var second = new ClientCase { Client = client, ResponsibleUserId = user.Id, Title = "Второе", Status = ClientCaseStatus.Open, CourtAwardedAmount = 1000m };
        seed.ClientCases.AddRange(first, second);
        var payment = new Payment { Client = client, ClientCase = first, DocumentKind = DocumentKind.Payment, Type = PaymentType.Income, Status = PaymentStatus.Completed, Amount = 1000m, PaidAmount = 1000m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow };
        seed.Payments.Add(payment);
        await seed.SaveChangesAsync();
        seed.ClientCaseCourtRecoveryPayments.Add(new ClientCaseCourtRecoveryPayment { PaymentId = payment.Id, ClientCaseId = first.Id, AllocatedAmount = 100m });
        await seed.SaveChangesAsync();

        var competingCommitted = false;
        var hook = new RecoveryBeforeSave(async ct =>
        {
            await using var other = Open();
            try
            {
                await using var transaction = await other.Database.BeginTransactionAsync(ct);
                if (postgres) await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
                if (operation == "paid")
                    await other.Payments.Where(row => row.Id == payment.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.PaidAmount, 150m), ct);
                else if (operation == "status")
                    await other.Payments.Where(row => row.Id == payment.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, PaymentStatus.Cancelled), ct);
                else if (operation == "association")
                    await other.Payments.Where(row => row.Id == payment.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, second.Id), ct);
                else if (operation == "award")
                    await other.ClientCases.Where(row => row.Id == first.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtAwardedAmount, 0m), ct);
                else if (operation == "permission")
                    await other.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                else
                {
                    await other.ClientCaseCourtRecoveryPayments.Where(row => row.PaymentId == payment.Id).ExecuteDeleteAsync(ct);
                    other.ClientCaseCourtRecoveryPayments.Add(new ClientCaseCourtRecoveryPayment { PaymentId = payment.Id, ClientCaseId = second.Id, AllocatedAmount = 700m });
                    await other.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                competingCommitted = true;
            }
            catch (Exception error) when (IsRecoveryTestLockConflict(error))
            {
                // Disposing the competing transaction rolls back only its own work.
            }
        });
        var sql = new RecoverySqlCapture();
        await using var request = Open(hook, sql);
        var controller = CreateController(request, user);
        var result = operation == "delete"
            ? await controller.RemoveCourtRecoveryPayment(first.Id, payment.Id, CancellationToken.None)
            : await controller.SetCourtRecoveryPayment(first.Id, payment.Id, new(500m), CancellationToken.None);
        Assert.True(hook.Called, "The competitor must execute after the original read and before the actual write.");
        Assert.True(result is NoContentResult or ConflictObjectResult, result.GetType().Name);
        Console.WriteLine($"RECOVERY_RACE provider={(postgres ? "postgres" : "sqlite")} operation={operation} competingCommitted={competingCommitted} response={result.GetType().Name}");
        foreach (var command in sql.Writes) Console.WriteLine(command);
        Assert.NotEmpty(sql.Writes);

        await using var verify = Open();
        var actual = await verify.ClientCaseCourtRecoveryPayments.AsNoTracking().SingleOrDefaultAsync(row => row.PaymentId == payment.Id);
        if (competingCommitted && operation is "update" or "delete")
        {
            Assert.NotNull(actual);
            Assert.Equal(second.Id, actual.ClientCaseId);
            Assert.Equal(700m, actual.AllocatedAmount);
            Assert.IsType<ConflictObjectResult>(result);
        }
        else if (competingCommitted)
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.NotNull(actual);
            Assert.Equal(100m, actual.AllocatedAmount);
            if (operation == "paid")
                Assert.Equal(150m, (await verify.Payments.AsNoTracking().SingleAsync(row => row.Id == payment.Id)).PaidAmount);
        }
        else
        {
            Assert.IsType<NoContentResult>(result);
            if (operation == "delete") Assert.Null(actual);
            else
            {
                Assert.NotNull(actual);
                Assert.Equal(first.Id, actual.ClientCaseId);
                Assert.Equal(500m, actual.AllocatedAmount);
            }
            Assert.Equal(1000m, (await verify.Payments.AsNoTracking().SingleAsync(row => row.Id == payment.Id)).PaidAmount);
        }
        Assert.Equal(1, await verify.Payments.CountAsync());
    }

    [Theory]
    [InlineData("paid", false)]
    [InlineData("permission", false)]
    [InlineData("permission", true)]
    [InlineData("inactive", false)]
    [InlineData("inactive", true)]
    [InlineData("unapproved", false)]
    [InlineData("unapproved", true)]
    public async Task CourtRecoveries_WriteRevalidatesTrackedReadsAndCachedAccess(string change, bool deleting)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "recovery-admission@example.local", "Юрист", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var clientCase = new ClientCase { Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id }, ResponsibleUserId = user.Id, Title = "Дело", Status = ClientCaseStatus.Open, CourtAwardedAmount = 1000m };
        var payment = new Payment { Client = clientCase.Client, ClientCase = clientCase, DocumentKind = DocumentKind.Payment, Type = PaymentType.Income, Status = PaymentStatus.Completed, Amount = 1000m, PaidAmount = 1000m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ClientCaseCourtRecoveryPayments.Add(new ClientCaseCourtRecoveryPayment { PaymentId = payment.Id, ClientCaseId = clientCase.Id, AllocatedAmount = 100m });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user);
        Assert.IsType<OkObjectResult>((await controller.GetCourtRecoveries(clientCase.Id, null, CancellationToken.None)).Result);
        if (change == "paid")
            await db.Payments.Where(row => row.Id == payment.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.PaidAmount, 150m));
        else if (change == "permission")
            await db.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        else if (change == "inactive")
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        else
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        var result = deleting
            ? await controller.RemoveCourtRecoveryPayment(clientCase.Id, payment.Id, CancellationToken.None)
            : await controller.SetCourtRecoveryPayment(clientCase.Id, payment.Id, new(500m), CancellationToken.None);
        if (change == "paid") Assert.IsType<BadRequestObjectResult>(result);
        else if (change == "permission") Assert.IsType<ForbidResult>(result);
        else Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(100m, (await db.ClientCaseCourtRecoveryPayments.AsNoTracking().SingleAsync()).AllocatedAmount);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtRecoveries_RejectsCallerTransactionOrPendingWorkWithoutDiscardingIt(bool ambient, bool deleting)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "recovery-dirty@example.local", "Юрист", DataVisibilityScope.All);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var pending = new Client { Name = "Несохранённая правка", IsActive = true };
        if (!ambient) db.Clients.Add(pending);
        var controller = CreateController(db, user);
        var result = deleting
            ? await controller.RemoveCourtRecoveryPayment(1, 1, CancellationToken.None)
            : await controller.SetCourtRecoveryPayment(1, 1, new(1m), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        if (ambient) Assert.Same(transaction, db.Database.CurrentTransaction);
        else Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.Empty(await db.Clients.AsNoTracking().ToArrayAsync());
    }

    private static bool IsRecoveryTestLockConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SqliteException { SqliteErrorCode: 5 or 6 }
                or PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }) return true;
        return false;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtRecoveries_ConflictingRequestOrSaveFailurePreservesAtomicWrite(bool postgres, bool failAfterSave)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var sqlite = $"Data Source=recovery-atomic-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = postgres ? null : new SqliteConnection(sqlite);
        if (anchor is not null) await anchor.OpenAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(database!.ConnectionString).AddInterceptors(interceptors).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(sqlite).AddInterceptors(interceptors).Options, useObjectAuditModel: true);
        await using var seed = Open();
        if (!postgres) await seed.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(seed, "recovery-atomic@example.local", "Юрист", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(seed, user, RolePermissionCatalog.Payments);
        var clientCase = new ClientCase { Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id }, ResponsibleUserId = user.Id, Title = "Дело", Status = ClientCaseStatus.Open, CourtAwardedAmount = 1000m };
        var payment = new Payment { Client = clientCase.Client, ClientCase = clientCase, DocumentKind = DocumentKind.Payment, Type = PaymentType.Income, Status = PaymentStatus.Completed, Amount = 1000m, PaidAmount = 1000m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow };
        seed.Payments.Add(payment);
        await seed.SaveChangesAsync();
        seed.ClientCaseCourtRecoveryPayments.Add(new ClientCaseCourtRecoveryPayment { PaymentId = payment.Id, ClientCaseId = clientCase.Id, AllocatedAmount = 100m });
        await seed.SaveChangesAsync();
        var auditCount = await seed.ObjectAuditEvents.CountAsync();
        var called = false;
        var before = new RecoveryBeforeSave(async ct =>
        {
            called = true;
            await using var competing = Open();
            var result = await CreateController(competing, user).SetCourtRecoveryPayment(clientCase.Id, payment.Id, new(700m), ct);
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Null(competing.Database.CurrentTransaction);
        });
        var after = new RecoveryAfterSaveFailure();
        await using var request = Open(failAfterSave ? after : before);
        var controller = CreateController(request, user);
        if (failAfterSave)
        {
            await Assert.ThrowsAsync<RecoverySaveFailureException>(() => controller.SetCourtRecoveryPayment(clientCase.Id, payment.Id, new(500m), CancellationToken.None));
            Assert.True(after.Called);
            Assert.Empty(request.ChangeTracker.Entries());
        }
        else
        {
            Assert.IsType<NoContentResult>(await controller.SetCourtRecoveryPayment(clientCase.Id, payment.Id, new(500m), CancellationToken.None));
            Assert.True(called);
        }
        Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        var link = await verify.ClientCaseCourtRecoveryPayments.AsNoTracking().SingleAsync();
        Assert.Equal(clientCase.Id, link.ClientCaseId);
        Assert.Equal(failAfterSave ? 100m : 500m, link.AllocatedAmount);
        Assert.Equal(1000m, (await verify.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        if (failAfterSave) Assert.Equal(auditCount, await verify.ObjectAuditEvents.CountAsync());
    }

    private sealed class RecoverySaveFailureException : Exception;

    private sealed class RecoveryAfterSaveFailure : SaveChangesInterceptor
    {
        public bool Called { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new RecoverySaveFailureException();
        }
    }

    private sealed class RecoveryBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        public bool Called { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && eventData.Context!.ChangeTracker.Entries<ClientCaseCourtRecoveryPayment>()
                .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted or EntityState.Added))
            {
                Called = true;
                await action(cancellationToken);
            }
            return result;
        }
    }

    private sealed class RecoverySqlCapture : DbCommandInterceptor
    {
        public List<string> Writes { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ClientCaseCourtRecoveryPayments", StringComparison.Ordinal)
                && (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal) || command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)))
                Writes.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
