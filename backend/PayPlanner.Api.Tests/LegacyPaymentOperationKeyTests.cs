using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Payments;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class LegacyPaymentOperationKeyTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> RoutesAndProviders()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var route in new[] { 0, 1, 2 }) yield return [postgres, route];
    }

    public static IEnumerable<object[]> InvalidHeaders()
    {
        foreach (var route in new[] { 0, 1, 2 })
            foreach (var kind in new[] { "missing", "empty", "whitespace", "zero", "compact", "malformed", "multiple", "comma" })
                yield return [route, kind];
    }

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public async Task MissingOrMalformedKey_RejectsWithoutRegisteringOrWriting(int route, string kind)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var header = kind switch
        {
            "missing" => StringValues.Empty,
            "empty" => new StringValues(""),
            "whitespace" => new StringValues(" " + Guid.NewGuid().ToString("D")),
            "zero" => new StringValues(Guid.Empty.ToString("D")),
            "compact" => new StringValues(Guid.NewGuid().ToString("N")),
            "malformed" => new StringValues("not-a-payment-operation-key"),
            "multiple" => new StringValues(new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") }),
            _ => new StringValues(Guid.NewGuid().ToString("D") + "," + Guid.NewGuid().ToString("D")),
        };
        var result = Assert.IsType<BadRequestObjectResult>(await CreateAsync(db, fixture, route, header));
        Assert.Equal("payment_operation_key_required", JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString());
        Assert.Single(await db.Payments.ToArrayAsync());
        Assert.Empty(await db.PaymentCreateAttempts.ToArrayAsync());
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task RegisteredKey_ReplaysCurrentCanonicalAndRejectsChangedPayload(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        await using var db = database.Open();
        var fixture = await SeedAsync(db, false);
        var key = await RegisterAsync(db, fixture);
        var created = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, route, Key(key))).Value);
        var canonicalId = created.Id;
        created.Description = "Changed after the original save";
        await db.SaveChangesAsync();
        var replay = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await CreateAsync(db, fixture, route, Key(key))).Value);
        Assert.Equal(canonicalId, replay.Id);
        Assert.Equal(created.Description, replay.Description);
        var changed = PaymentFor(fixture); changed.Amount += 1m;
        Assert.IsType<ConflictObjectResult>(await CreateAsync(db, fixture, route, Key(key), changed));
        Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
        var attempt = await db.PaymentCreateAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentCreateAttempt.Committed, attempt.State);
        Assert.Equal(canonicalId, attempt.CanonicalPaymentId);
        Assert.Equal(64, attempt.PayloadIdentity!.Length);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task CommittedKey_CannotBeReusedThroughAnotherRoute(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        await using var db = database.Open();
        var fixture = await SeedAsync(db, false); var key = await RegisterAsync(db, fixture);
        Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, route, Key(key)));
        Assert.IsType<ConflictObjectResult>(await CreateAsync(db, fixture, (route + 1) % 3, Key(key)));
        Assert.Equal(2, await db.Payments.CountAsync());
    }

    public static IEnumerable<object[]> UnavailableKeys()
    {
        foreach (var row in RoutesAndProviders())
            foreach (var state in new[] { "unknown", "cancelled", "expired", "otherActor", "otherSeller", "remainder" })
                yield return [row[0], row[1], state];
    }

    [Theory]
    [MemberData(nameof(UnavailableKeys))]
    public async Task UnavailableOrWrongScopeKey_DoesNotCreatePayment(bool postgres, int route, string state)
    {
        await using var database = await DatabaseAsync(postgres);
        await using var db = database.Open();
        var fixture = await SeedAsync(db, false); var key = await RegisterAsync(db, fixture);
        var row = await db.PaymentCreateAttempts.SingleAsync();
        if (state == "unknown") key = Guid.NewGuid();
        else if (state == "cancelled") { row.State = PaymentCreateAttempt.Cancelled; row.CancelledAtUtc = DateTimeOffset.UtcNow; }
        else if (state == "expired") row.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        else if (state == "otherActor") row.ActorUserId = fixture.ActorId + 100;
        else if (state == "otherSeller") row.SellerCompanyId = 100;
        else { row.Operation = "remainder"; row.PrimaryPaymentId = fixture.PaymentId; }
        await db.SaveChangesAsync();
        var result = await CreateAsync(db, fixture, route, Key(key));
        if (state is "otherSeller" or "remainder") Assert.IsType<ConflictObjectResult>(result);
        else Assert.Equal(410, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Single(await db.Payments.ToArrayAsync());
        Assert.Single(await db.PaymentCreateAttempts.ToArrayAsync());
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task LostCommitAcknowledgement_SameKeyRecoversOnePayment(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        Fixture fixture; Guid key;
        await using (var seed = database.Open()) { fixture = await SeedAsync(seed, false); key = await RegisterAsync(seed, fixture); }
        var fault = new LoseAcknowledgement();
        await using (var writer = database.Open(fault))
            await Assert.ThrowsAsync<IOException>(() => CreateAsync(writer, fixture, route, Key(key)));
        await using var fresh = database.Open();
        var result = Assert.IsType<OkObjectResult>(await CreateAsync(fresh, fixture, route, Key(key)));
        Assert.True(Assert.IsType<Payment>(result.Value).Id > 0);
        Assert.Equal(2, await fresh.Payments.CountAsync());
        Assert.Equal(PaymentCreateAttempt.Committed, (await fresh.PaymentCreateAttempts.SingleAsync()).State);
        Assert.Single(await fresh.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task FailedOutcomeWrite_RollsBackPaymentAndKeepsOriginalKeyUsable(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        Fixture fixture; Guid key;
        await using (var seed = database.Open()) { fixture = await SeedAsync(seed, false); key = await RegisterAsync(seed, fixture); }
        await using (var writer = database.Open(new FailOutcomeWrite()))
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => CreateAsync(writer, fixture, route, Key(key)));
            Assert.IsType<IOException>(failure.InnerException);
        }
        await using var fresh = database.Open();
        Assert.Single(await fresh.Payments.ToArrayAsync());
        Assert.Equal(PaymentCreateAttempt.Draft, (await fresh.PaymentCreateAttempts.SingleAsync()).State);
        Assert.IsType<CreatedResult>(await CreateAsync(fresh, fixture, route, Key(key)));
        Assert.Equal(2, await fresh.Payments.CountAsync());
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task Replay_RechecksPermissionAndVisibilityAndNeverRecreatesDeletedPayment(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        await using var db = database.Open();
        var fixture = await SeedAsync(db, false); var key = await RegisterAsync(db, fixture);
        var payment = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, route, Key(key))).Value);
        var permission = await db.RolePermissions.SingleAsync(row => row.Section == "payments");
        permission.CanCreate = false; await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await CreateAsync(db, fixture, route, Key(key)));
        permission = await db.RolePermissions.SingleAsync(row => row.Section == "payments");
        permission.CanCreate = true; permission.ViewScope = "own"; await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await CreateAsync(db, fixture, route, Key(key)));
        permission = await db.RolePermissions.SingleAsync(row => row.Section == "payments");
        permission.ViewScope = "all"; await db.SaveChangesAsync();
        var canonical = await db.Payments.SingleAsync(row => row.Id == payment.Id);
        db.Payments.Remove(canonical); await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await CreateAsync(db, fixture, route, Key(key)));
        Assert.Single(await db.Payments.ToArrayAsync());
        Assert.Equal(payment.Id, (await db.PaymentCreateAttempts.SingleAsync()).CanonicalPaymentId);
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task NotificationFailure_AfterCommitReplaysWithoutAnotherDeliveryOrBalanceWrite(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        await using var db = database.Open();
        var fixture = await SeedAsync(db, false);
        (await db.Clients.SingleAsync()).ResponsibleUserId = fixture.ActorId;
        await db.SaveChangesAsync();
        var key = await RegisterAsync(db, fixture);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create();
        var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)notifications;
        probe.Before = async _ =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await using var observer = database.Open();
            Assert.Equal(2, await observer.Payments.CountAsync());
            Assert.Equal(PaymentCreateAttempt.Committed, (await observer.PaymentCreateAttempts.SingleAsync()).State);
            throw new IOException("Synthetic notification failure after payment commit");
        };
        Payment Model()
        {
            var model = PaymentFor(fixture);
            model.PaidAmount = model.Amount; model.IsPaid = true;
            model.PaidDate = model.Date; model.LastPaymentDate = model.Date; model.Status = PaymentStatus.Completed;
            return model;
        }
        if (route == 1) Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, route, Key(key), Model(), notifications));
        else await Assert.ThrowsAsync<IOException>(() => CreateAsync(db, fixture, route, Key(key), Model(), notifications));
        Assert.NotEmpty(probe.Calls);
        var delivered = probe.Calls.Count;
        var balances = JsonSerializer.Serialize(await db.ClientBalanceTransactions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        Assert.IsType<OkObjectResult>(await CreateAsync(db, fixture, route, Key(key), Model(), notifications));
        Assert.Equal(delivered, probe.Calls.Count);
        Assert.Equal(balances, JsonSerializer.Serialize(await db.ClientBalanceTransactions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
        Assert.Equal(2, await db.Payments.CountAsync());
    }

    [Theory]
    [MemberData(nameof(RoutesAndProviders))]
    public async Task ConcurrentSameKey_FailsBusyRequestThenReplaysTheOneCommittedPayment(bool postgres, int route)
    {
        await using var database = await DatabaseAsync(postgres);
        Fixture fixture; Guid key;
        await using (var seed = database.Open()) { fixture = await SeedAsync(seed, false); key = await RegisterAsync(seed, fixture); }
        var hold = new HoldCommit();
        await using var writer = database.Open(hold);
        var pending = CreateAsync(writer, fixture, route, Key(key));
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var contender = database.Open();
            Assert.IsType<ConflictObjectResult>(await CreateAsync(contender, fixture, route, Key(key)).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { hold.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var fresh = database.Open();
        Assert.IsType<OkObjectResult>(await CreateAsync(fresh, fixture, route, Key(key)));
        Assert.Equal(2, await fresh.Payments.CountAsync());
        Assert.Equal(PaymentCreateAttempt.Committed, (await fresh.PaymentCreateAttempts.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOperation_DoesNotGrantModernRemainderEditExemption(bool postgres)
    {
        await using var database = await DatabaseAsync(postgres);
        await using var db = database.Open();
        var fixture = await SeedAsync(db, false); var key = await RegisterAsync(db, fixture);
        var paid = PaymentFor(fixture);
        paid.PaidAmount = paid.Amount; paid.IsPaid = true; paid.PaidDate = paid.Date;
        paid.LastPaymentDate = paid.Date; paid.Status = PaymentStatus.Completed;
        var source = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, 0, Key(key), paid)).Value);
        (await db.RolePermissions.SingleAsync(row => row.Section == "payments")).CanEdit = false;
        await db.SaveChangesAsync();
        var configured = new ServiceCollection();
        configured.AddOptions<JsonOptions>().Configure(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        using var services = configured.BuildServiceProvider();
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        controller.HttpContext.RequestServices = services;
        var registration = JsonSerializer.SerializeToElement(new
        {
            operation = "remainder",
            sellerCompanyId = (int?)null,
            sellerBranchId = (int?)null,
            primaryPaymentId = source.Id,
        });
        var remainder = Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(
            await controller.RegisterCreateAttempt(registration, default)).Value);
        var command = JsonSerializer.SerializeToElement(new
        {
            date = "2026-11-02",
            plannedDate = "2026-11-02",
            amount = 10m,
            paidAmount = 0m,
            type = "Income",
            status = "Pending",
            description = "Remainder permission check",
            isPaid = false,
            notes = "",
            systemNotes = "",
            rescheduleCount = 0,
            clientId = fixture.OwnerId,
        });
        Assert.IsType<ForbidResult>(await controller.CommitCreateAttempt(remainder.AttemptId, command, default));
        Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.SingleAsync(row => row.Id == remainder.AttemptId)).State);
    }

    [Fact]
    public void Identity_CoversEveryLogicalInputAndNormalizesDecimalScale()
    {
        var protocol = "legacy-payment-create-v1";
        var original = new Payment();
        var identity = Identity(original, protocol);
        foreach (var property in LogicalInputProperties())
        {
            var changed = new Payment();
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value = type == typeof(string) ? "different input"
                : type == typeof(decimal) ? 1m
                : type == typeof(int) ? 1
                : type == typeof(bool) ? !(bool)property.GetValue(changed)!
                : type == typeof(DateTime) ? new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                : type.IsEnum ? Enum.GetValues(type).Cast<object>().First(item => !Equals(item, property.GetValue(changed)))
                : throw new InvalidOperationException("Unreviewed logical payment input: " + property.Name);
            property.SetValue(changed, value);
            Assert.NotEqual(identity, Identity(changed, protocol));
        }
        original.Amount = 1.00m;
        var equivalent = new Payment { Amount = 1m, CreatedAt = DateTime.UtcNow.AddDays(-10) };
        Assert.Equal(Identity(original, protocol), Identity(equivalent, protocol));
        Assert.NotEqual(identity, Identity(new Payment(), "legacy-v1-payment-create-v1"));
    }

    // Keep the mechanism internal in production; public-route tests above cover
    // persisted outcomes, and this narrow reflection check covers field identity.
    private static string Identity(Payment model, string protocol)
        => (string)typeof(PayPlanner.Api.Controllers.PaymentsController).Assembly
            .GetType("PayPlanner.Api.Services.Payments.LegacyPaymentCreateAttempt", throwOnError: true)!
            .GetMethod("BuildIdentity", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [model, protocol])!;

    private static IReadOnlyList<PropertyInfo> LogicalInputProperties()
        => (IReadOnlyList<PropertyInfo>)typeof(PayPlanner.Api.Controllers.PaymentsController).Assembly
            .GetType("PayPlanner.Api.Services.Payments.PaymentCreateInputRules", throwOnError: true)!
            .GetProperty("LogicalInputProperties", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    internal static async Task<Guid> RegisterAsync(PaymentContext db, Fixture fixture, Payment? model = null)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        controller.HttpContext.RequestServices = services;
        var body = JsonSerializer.SerializeToElement(new
        {
            operation = "primary",
            sellerCompanyId = model?.SellerCompanyId,
            sellerBranchId = model?.SellerBranchId,
            primaryPaymentId = (int?)null
        });
        return Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(
            await controller.RegisterCreateAttempt(body, default)).Value).AttemptId;
    }

    private static StringValues Key(Guid key) => new(key.ToString("D", CultureInfo.InvariantCulture));

    internal static Task<IActionResult> CreateAsync(PaymentContext db, Fixture fixture, int route, StringValues header,
        Payment? model = null, INotificationService? notifications = null)
    {
        var current = BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications);
        current.Request.Headers["Idempotency-Key"] = header;
        model ??= PaymentFor(fixture);
        return route switch
        {
            0 => current.Create(model, default),
            1 => new PaymentsV1Controller(db, notifications) { ControllerContext = current.ControllerContext }.Create(model),
            2 => new PaymentsV2Controller(db, notifications) { ControllerContext = current.ControllerContext }.Create(model),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    private async Task<Database> DatabaseAsync(bool postgres)
    {
        if (postgres)
        {
            var lease = await template.CreateDatabaseAsync();
            return new(true, lease.ConnectionString, lease);
        }
        var connectionString = $"Data Source=legacy_operation_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        try
        {
            await using var source = await SqliteTestContextFactory.CreateAsync();
            ((SqliteConnection)source.Database.GetDbConnection()).BackupDatabase(anchor);
            return new(false, connectionString, anchor);
        }
        catch { await anchor.DisposeAsync(); throw; }
    }

    private sealed class Database(bool postgres, string connectionString, IAsyncDisposable lease) : IAsyncDisposable
    {
        internal PaymentContext Open(IInterceptor? interceptor = null)
        {
            if (postgres)
            {
                var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString);
                if (interceptor is not null) options.AddInterceptors(interceptor);
                return new PostgreSqlPaymentContext(options.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString);
            if (interceptor is not null) sqlite.AddInterceptors(interceptor);
            return new PaymentContext(sqlite.Options);
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private sealed class LoseAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
            => throw new IOException("Synthetic lost payment acknowledgement");
    }

    private sealed class FailOutcomeWrite : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("UPDATE \"PaymentCreateAttempts\"", StringComparison.Ordinal))
                throw new IOException("Synthetic payment operation outcome failure");
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    private sealed class HoldCommit : DbTransactionInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }
}
