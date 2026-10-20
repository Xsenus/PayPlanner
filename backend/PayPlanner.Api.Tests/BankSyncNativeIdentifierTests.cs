using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    public static IEnumerable<object[]> NativeInvalidIdentifierCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "null", "empty", "whitespace", "overlong", "embedded-null" }
        from booked in new[] { false, true }
        from scheduled in new[] { false, true }
        select new object[] { postgres, scenario, booked, scheduled };

    public static IEnumerable<object[]> NativeIdentifierBoundaryCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "maximum", "trimmed-maximum", "distinct-maximum", "pending-maximum" }
        from scheduled in new[] { false, true }
        select new object[] { postgres, scenario, scheduled };

    public static IEnumerable<object[]> NativeLateIdentifierCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "empty", "overlong" }
        from scheduled in new[] { false, true }
        select new object[] { postgres, scenario, scheduled };

    private static string? InvalidNativeIdentifier(string scenario) => scenario switch
    {
        "null" => null,
        "empty" => "",
        "whitespace" => " \t\r\n ",
        "overlong" => new string('i', 129),
        "embedded-null" => "native\0identifier",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static BankSyncInvocation IdentifierInvocation(ClaimsPrincipal principal, bool scheduled, string name)
        => scheduled ? BankSyncInvocation.Scheduled(name) : BankSyncInvocation.ForUser(principal, Guid.NewGuid());

    private static async Task AssertNativeIdentifierFailureAsync(PaymentContext db, BankStatementSyncResult result,
        string scenario, int preserved)
    {
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.CountAsync();
        var money = await db.MoneyOperations.IgnoreQueryFilters().CountAsync();
        Assert.False(string.IsNullOrWhiteSpace(result.Error),
            $"Unsupported native ID must not be silently omitted; scenario={scenario}; persisted raw={raw}, money={money}");
        Assert.Contains("банк", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(result.Error!.Length, 1, 1000);
        Assert.Equal(preserved, raw);
        Assert.Equal(preserved, money);
        Assert.Equal(preserved, await db.BankStatementImportRows.CountAsync());
        Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
        Assert.NotNull(result.BatchId);
        var batch = await db.BankStatementImportBatches.AsNoTracking().SingleAsync(item => item.Id == result.BatchId);
        Assert.Null(batch.CompletedAt);
        Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, batch.Status);
        Assert.Equal("error", (await db.BankSyncLogs.AsNoTracking().Where(item => item.Kind == "statement")
            .OrderByDescending(item => item.Id).FirstAsync()).Result);
    }

    [Theory]
    [MemberData(nameof(NativeInvalidIdentifierCases))]
    public async Task SharedNative_InvalidIdentifierCannotSilentlyCommitNeighborAndCanResumeOriginalRequest(
        bool postgres, string scenario, bool booked, bool scheduled)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (_, principal) = await AddNativeAdminAsync(db);
        var invocation = IdentifierInvocation(principal, scheduled, "invalid-identifier");
        var bad = Op("placeholder", 2, 200m, booked) with { ExternalId = InvalidNativeIdentifier(scenario)! };
        fixture.Bank.Pages["shared"] = [[Op("identifier-neighbor", 1), bad]];
        int? batchId = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
            await AssertNativeIdentifierFailureAsync(db, failed, scenario, 0);
            batchId ??= failed.BatchId;
            Assert.Equal(batchId, failed.BatchId);
        }
        fixture.Bank.Pages["shared"] = [[Op("identifier-neighbor", 1), Op("corrected-identifier", 2, 200m, booked)]];
        var resumed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(batchId, resumed.BatchId);
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, raw.Length);
        Assert.Contains(raw, item => item.ExternalOperationId == "corrected-identifier" && item.IsBooked == booked);
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(booked ? 2 : 1, money.Length);
        Assert.Equal(2, await db.BankStatementImportRows.CountAsync());
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
        var calls = fixture.Bank.StatementCalls;
        var replay = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Equal(resumed, replay);
        Assert.Equal(calls, fixture.Bank.StatementCalls);
        Assert.Equal(money.Select(item => item.Id), await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(NativeIdentifierBoundaryCases))]
    public async Task SharedNative_IdentifierBoundaryPreservesExactIdentityAndPendingTransition(
        bool postgres, string scenario, bool scheduled)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (_, principal) = await AddNativeAdminAsync(db);
        var identifier = new string('i', 128);
        var distinct = scenario == "distinct-maximum";
        var pending = scenario == "pending-maximum";
        fixture.Bank.Pages["shared"] = distinct
            ? [[Op(new string('i', 127) + "a", 2), Op(new string('i', 127) + "b", 2)]]
            : [[Op(scenario == "trimmed-maximum" ? " \t" + identifier + "\r\n" : identifier, 2, booked: !pending)]];
        var firstInvocation = IdentifierInvocation(principal, scheduled, "identifier-boundary-first");
        var first = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, firstInvocation, default);
        Assert.Null(first.Error);
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(distinct ? 2 : 1, raw.Length);
        Assert.All(raw, item => Assert.Equal(128, item.ExternalOperationId.Length));
        if (!distinct) Assert.Equal(identifier, raw[0].ExternalOperationId);
        Assert.Equal(pending ? 0 : distinct ? 2 : 1, await db.MoneyOperations.CountAsync());
        if (pending)
        {
            Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
            fixture.Bank.Pages["shared"] = [[Op(identifier, 2)]];
            Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
                IdentifierInvocation(principal, scheduled, "identifier-boundary-booked"), default)).Error);
            db.ChangeTracker.Clear();
            var confirmed = await db.BankStatementOperations.AsNoTracking().SingleAsync();
            Assert.Equal(raw[0].Id, confirmed.Id);
            Assert.True(confirmed.IsBooked);
            Assert.Single(await db.MoneyOperations.ToArrayAsync());
        }
        var ids = await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            IdentifierInvocation(principal, scheduled, "identifier-boundary-overlap"), default)).Error);
        db.ChangeTracker.Clear();
        Assert.Equal(raw.Select(item => item.Id), await db.BankStatementOperations.AsNoTracking()
            .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal(ids, await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(NativeLateIdentifierCases))]
    public async Task SharedNative_InvalidLaterIdentifierKeepsEarlierPageAndResumesWithoutSecondFact(
        bool postgres, string scenario, bool scheduled)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (_, principal) = await AddNativeAdminAsync(db);
        var invocation = IdentifierInvocation(principal, scheduled, "late-invalid-identifier");
        var bad = Op("placeholder", 3) with { ExternalId = InvalidNativeIdentifier(scenario)! };
        fixture.Bank.Pages["shared"] = [[Op("identifier-preserved-first-page", 1)], [Op("identifier-late-neighbor", 2), bad]];
        var failed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        await AssertNativeIdentifierFailureAsync(db, failed, scenario, 1);
        var originalMoney = await db.MoneyOperations.AsNoTracking().SingleAsync();
        var originalRaw = await db.BankStatementOperations.AsNoTracking().SingleAsync();
        fixture.Bank.Pages["shared"] = [[Op("identifier-preserved-first-page", 1)], [Op("identifier-late-neighbor", 2), Op("identifier-late-corrected", 3)]];
        var resumed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(failed.BatchId, resumed.BatchId);
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.MoneyOperations.CountAsync());
        Assert.Equal(3, await db.BankStatementOperations.CountAsync());
        Assert.Equal(3, await db.BankStatementImportRows.CountAsync());
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
        Assert.True(await db.MoneyOperations.AnyAsync(item => item.Id == originalMoney.Id && item.SourceMovementKey == originalMoney.SourceMovementKey));
        Assert.True(await db.BankStatementOperations.AnyAsync(item => item.Id == originalRaw.Id && item.ExternalOperationId == originalRaw.ExternalOperationId));
        Assert.Equal(resumed, await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default));
        Assert.Equal(3, await db.MoneyOperations.CountAsync());
    }
}
