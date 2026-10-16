using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services;

internal sealed record MoneyReconciliationScope(int[] CompanyIds, bool AllowUnknownLegacy, int? ActorId)
{
    public static Task<MoneyReconciliationScope> ForUserAsync(PaymentContext db,
        BankStatementImportAccessContext access, CancellationToken ct)
        => CreateAsync(db, access.AllowedCompanyIds, access.Visibility.IsAdmin, access.Visibility.UserId, ct);

    public static async Task<MoneyReconciliationScope> ForSystemAsync(PaymentContext db, CancellationToken ct)
        => await CreateAsync(db, (await OwnCompanyRegistry.LoadIdsAsync(db, ct)).ToArray(), true, null, ct);

    private static async Task<MoneyReconciliationScope> CreateAsync(PaymentContext db, int[] requested,
        bool allowUnknown, int? actorId, CancellationToken ct)
    {
        var companies = await db.LegalEntities.AsNoTracking().Where(item => requested.Contains(item.Id))
            .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(ct);
        return new(companies, allowUnknown, actorId);
    }

    public IQueryable<MoneyOperationQueryRow> FilterRows(IQueryable<MoneyOperationQueryRow> query)
        => query.Where(item => item.CompanyId.HasValue ? CompanyIds.Contains(item.CompanyId.Value) : AllowUnknownLegacy);

    public IQueryable<MoneyAccount> Accounts(PaymentContext db)
        => BankStatementImportAccess.CoherentAccounts(db, db.MoneyAccounts.AsNoTracking()
            .Where(account => account.CompanyId.HasValue && CompanyIds.Contains(account.CompanyId.Value)));

    public MoneyJournalQuery CreateQuery(PaymentContext db)
    {
        var accounts = Accounts(db);
        var money = db.MoneyOperations.AsNoTracking().Where(operation => operation.MoneyAccountId.HasValue
            ? accounts.Any(account => account.Id == operation.MoneyAccountId)
            : AllowUnknownLegacy);
        var payments = BankStatementImportAccess.ScopePaymentCompanies(db, db.Payments.AsNoTracking(), CompanyIds);
        return new(db, new(money, payments, db.MoneyOperations.Where(_ => false), false));
    }

    public IQueryable<MoneyBalanceReconciliationResult> ReadResults(PaymentContext db)
    {
        var query = db.MoneyBalanceReconciliationResults.AsNoTracking();
        if (AllowUnknownLegacy) return query; // Database-verified admin audit, never employee-All.
        var accounts = Accounts(db);
        return query.Where(row => row.ScopeKey != null && row.CompanyId.HasValue && CompanyIds.Contains(row.CompanyId.Value)
            && (!row.MoneyAccountId.HasValue || accounts.Any(account => account.Id == row.MoneyAccountId && account.CompanyId == row.CompanyId)));
    }

    public bool SameAuthority(MoneyReconciliationScope other)
        => ActorId == other.ActorId && AllowUnknownLegacy == other.AllowUnknownLegacy && CompanyIds.SequenceEqual(other.CompanyIds);
}
