using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;

namespace PayPlanner.Api.Controllers;

public partial class MoneyOperationsController
{
    private BankStatementImportAccessContext? moneyAccess;

    private async Task<BankStatementImportAccessContext?> ResolveMoneyAccessAsync(CancellationToken ct)
        => moneyAccess = await BankStatementImportAccess.ResolveAsync(_db, User, ct);

    private IQueryable<MoneyAccount> CompanyMoneyAccounts(BankStatementImportAccessContext access, bool forNewOperations)
        => BankStatementImportAccess.CoherentAccounts(_db,
            BankStatementImportAccess.ScopeAccounts(_db.MoneyAccounts.AsNoTracking(), access, ignoreQueryFilters: false), forNewOperations);

    private IQueryable<MoneyOperation> ScopeCompanyMoneyOperations(IQueryable<MoneyOperation> query)
        => BankStatementImportAccess.ScopeMoneyOperations(_db, query, moneyAccess);

    private IQueryable<Payment> ScopeCompanyPaymentOperations(IQueryable<Payment> query)
    {
        if (moneyAccess is null) return query.Where(_ => false);
        if (moneyAccess.Visibility.IsAdmin) return query;
        return BankStatementImportAccess.ScopePaymentCompanies(_db, query, moneyAccess.AllowedCompanyIds);
    }

    private sealed record MoneyAccountLease(IReadOnlyDictionary<int, MoneyAccount> Accounts, ActionResult? Error);

    private async Task<MoneyAccountLease> LeaseMoneyAccountsAsync(IEnumerable<int> requestedIds,
        Func<RolePermissionValues, bool> isAllowed, CancellationToken ct, bool allowExistingBankState = false)
    {
        MoneyAccountLease Denied(ActionResult error) => new(new Dictionary<int, MoneyAccount>(), error);
        var ids = requestedIds.Distinct().Order().ToArray();
        if (ids.Length is < 1 or > 2 || ids.Any(id => id <= 0))
            return Denied(BadRequest("Выберите денежный счёт своей компании."));
        var access = moneyAccess ?? await ResolveMoneyAccessAsync(ct);
        if (access is null || !access.Permission.CanView || !isAllowed(access.Permission)) return Denied(Forbid());
        var query = CompanyMoneyAccounts(access, forNewOperations: !allowExistingBankState);
        if (allowExistingBankState) query = BankStatementImportAccess.ActiveAccountState(query);
        var accounts = await query.Where(account => ids.Contains(account.Id)).ToDictionaryAsync(account => account.Id, ct);
        if (accounts.Count != ids.Length) return Denied(BadRequest("Денежный счёт недоступен для этой операции. Обновите список счетов."));

        // Use the same company -> bank account -> money account order as account CRUD.
        foreach (var companyId in accounts.Values.Select(account => account.CompanyId!.Value).Distinct().Order())
            if (await _db.LegalEntities.Where(company => company.Id == companyId && company.ProfileStatus == CompanyProfileStatuses.Active)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1)
                return Denied(BadRequest("Профиль собственной компании изменён. Обновите список счетов."));
        foreach (var account in accounts.Values.Where(account => account.BankConnectionAccountId.HasValue).OrderBy(account => account.BankConnectionAccountId))
            if (await _db.BankConnectionAccounts.Where(bank => bank.Id == account.BankConnectionAccountId
                    && bank.BankConnection.LegalEntityId == account.CompanyId && bank.AccountNumber == account.BankAccountNumber
                    && (bank.Bik ?? "") == (account.BankBik ?? "") && bank.Currency == account.Currency)
                .ExecuteUpdateAsync(setters => setters.SetProperty(bank => bank.ExternalAccountId, bank => bank.ExternalAccountId), ct) != 1)
                return Denied(BadRequest("Банковская привязка изменена. Обновите список счетов."));
        foreach (var account in accounts.Values.OrderBy(account => account.Id))
            if (await _db.MoneyAccounts.Where(current => current.Id == account.Id && current.CompanyId == account.CompanyId
                    && current.Source == account.Source && current.Currency == account.Currency && current.Name == account.Name
                    && current.BankConnectionAccountId == account.BankConnectionAccountId
                    && current.BankAccountNumber == account.BankAccountNumber && current.BankBik == account.BankBik && current.IsActive)
                .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.Name, current => current.Name), ct) != 1)
                return Denied(BadRequest("Денежный счёт изменён. Обновите список счетов."));
        if (await RefreshMoneyMutationAccessAsync(accounts.Values, isAllowed, ct) is { } changedAccess) return Denied(changedAccess);
        var freshQuery = CompanyMoneyAccounts(moneyAccess!, forNewOperations: !allowExistingBankState);
        if (allowExistingBankState) freshQuery = BankStatementImportAccess.ActiveAccountState(freshQuery);
        if (await freshQuery.CountAsync(account => ids.Contains(account.Id), ct) != ids.Length)
            return Denied(BadRequest("Доступность денежного счёта изменилась. Обновите список счетов."));
        return new(accounts, null);
    }

    private async Task<ActionResult?> RefreshMoneyMutationAccessAsync(IEnumerable<MoneyAccount> accounts,
        Func<RolePermissionValues, bool> isAllowed, CancellationToken ct)
    {
        var access = await ResolveMoneyAccessAsync(ct);
        return access is null || !access.Permission.CanView || !isAllowed(access.Permission)
            || accounts.Any(account => !account.CompanyId.HasValue || !access.AllowedCompanyIds.Contains(account.CompanyId.Value))
            ? Forbid() : null;
    }

    private static bool MoneyFactsChanged(MoneyOperation original, MoneyOperation next)
        => original.MoneyAccountId != next.MoneyAccountId || original.Source != next.Source
            || original.Direction != next.Direction || original.AccountingType != next.AccountingType
            || original.Amount != next.Amount || original.Date.Date != next.Date.Date
            || !string.Equals(original.Currency?.Trim(), next.Currency?.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<MoneyOperation?> LockMoneyOperationAsync(MoneyOperation snapshot, CancellationToken ct)
    {
        if (await _db.MoneyOperations.Where(operation => operation.Id == snapshot.Id
                && operation.MoneyAccountId == snapshot.MoneyAccountId && operation.UpdatedAt == snapshot.UpdatedAt
                && operation.BankStatementImportBatchId == snapshot.BankStatementImportBatchId
                && operation.SourceMovementKey == snapshot.SourceMovementKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(operation => operation.Purpose, operation => operation.Purpose), ct) != 1)
            return null;
        var tracked = _db.MoneyOperations.Local.FirstOrDefault(operation => operation.Id == snapshot.Id);
        if (tracked is not null) await _db.Entry(tracked).ReloadAsync(ct);
        return await _db.MoneyOperations.SingleAsync(operation => operation.Id == snapshot.Id, ct);
    }

    private sealed class MoneyMutationScope(PaymentContext db, IDbContextTransaction transaction,
        bool ownsTransaction, string? savepoint) : IAsyncDisposable
    {
        private bool completed;

        public static async Task<MoneyMutationScope> BeginAsync(PaymentContext db, CancellationToken ct)
        {
            if (db.Database.CurrentTransaction is not { } current)
                return new(db, await db.Database.BeginTransactionAsync(ct), true, null);
            var savepoint = "manual_money_" + Guid.NewGuid().ToString("N");
            await current.CreateSavepointAsync(savepoint, ct);
            return new(db, current, false, savepoint);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (ownsTransaction) await transaction.CommitAsync(ct);
            else await transaction.ReleaseSavepointAsync(savepoint!, ct);
            completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!completed && !ownsTransaction)
                {
                    await transaction.RollbackToSavepointAsync(savepoint!, CancellationToken.None);
                    await transaction.ReleaseSavepointAsync(savepoint!, CancellationToken.None);
                }
            }
            finally
            {
                if (!completed) db.ChangeTracker.Clear();
                if (ownsTransaction) await transaction.DisposeAsync();
            }
        }
    }
}
