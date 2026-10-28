using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services.Banking;

public sealed record BankStatementImportAccessContext(
    SectionAccessContext Section,
    int[] OwnCompanyIds,
    int[] AllowedCompanyIds)
{
    private static readonly RolePermissionValues LegacyPaymentPermission =
        RolePermissionCatalog.BuildDefaults(allowRoleManagement: false)[RolePermissionCatalog.Payments];

    public DataVisibilityAccess Visibility => Section.Visibility;
    public RolePermissionValues Permission => Section.Permission ?? LegacyPaymentPermission;
}

/// <summary>One database scope for package counts, history and protected mutations.</summary>
public static class BankStatementImportAccess
{
    public static IQueryable<CashOrder> ScopeCashOrderCompanies(PaymentContext db,
        IQueryable<CashOrder> query, BankStatementImportAccessContext? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.Visibility.IsAdmin) return query;
        var money = ScopeMoneyOperations(db, db.MoneyOperations.AsNoTracking(), access);
        var payments = ScopePaymentCompanies(db, db.Payments.AsNoTracking(), access.AllowedCompanyIds);
        var accounts = CoherentAccounts(db, ScopeAccounts(db.MoneyAccounts.AsNoTracking(), access, ignoreQueryFilters: false));
        // The actual money source is authoritative. A document reference cannot
        // reassign an existing movement to another company or hide its own history.
        // Without a movement, use the payment's company policy; never infer ownership
        // from an account label or the current employee's company.
        return query.Where(order =>
            ((!order.MoneyAccountId.HasValue && !order.CompanyId.HasValue)
                || (order.MoneyAccountId.HasValue && order.CompanyId.HasValue
                    && accounts.Any(account => account.Id == order.MoneyAccountId && account.CompanyId == order.CompanyId)))
            && (order.MoneyOperationId.HasValue
                ? money.Any(operation => operation.Id == order.MoneyOperationId.Value)
                : order.PaymentId.HasValue
                    ? payments.Any(payment => payment.Id == order.PaymentId.Value)
                    : !order.InvoiceId.HasValue || !db.Payments.Any(payment => payment.InvoiceId == order.InvoiceId
                        && !payments.Any(permitted => permitted.Id == payment.Id))));
    }

    public static IQueryable<MoneyOperation> ScopeMoneyOperations(PaymentContext db,
        IQueryable<MoneyOperation> query, BankStatementImportAccessContext? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.Visibility.IsAdmin) return query;
        var accounts = CoherentAccounts(db,
            ScopeAccounts(db.MoneyAccounts.AsNoTracking(), access, ignoreQueryFilters: false));
        // Ordinary legacy rows retain the caller's responsible-user visibility.
        // An imported unknown origin must not be inferred from its account name.
        return query.Where(operation => operation.MoneyAccountId.HasValue
            ? accounts.Any(account => account.Id == operation.MoneyAccountId.Value)
            : !operation.BankStatementImportBatchId.HasValue && operation.SourceMovementKey == null);
    }

    internal static IQueryable<TDocument> ScopeKnownDocumentCompanies<TDocument>(PaymentContext db,
        IQueryable<TDocument> query, int[] allowed) where TDocument : class, IDocumentSeller
    {
        var companies = db.LegalEntities.AsNoTracking().Where(company => allowed.Contains(company.Id));
        if (db.Model.FindEntityType(typeof(LegalEntity))?.FindProperty("IsDeleted") is not null)
            companies = companies.Where(company => !EF.Property<bool>(company, "IsDeleted"));
        var branches = UndeletedCompanyBranches(db);
        // An unknown document seller is never inferred from a money row or a name.
        return query.Where(document => EF.Property<int?>(document, nameof(IDocumentSeller.SellerCompanyId)).HasValue
            && companies.Any(company => company.Id == EF.Property<int?>(document, nameof(IDocumentSeller.SellerCompanyId)))
            && (!EF.Property<int?>(document, nameof(IDocumentSeller.SellerBranchId)).HasValue
                || branches.Any(branch => branch.Id == EF.Property<int?>(document, nameof(IDocumentSeller.SellerBranchId))
                    && branch.LegalEntityId == EF.Property<int?>(document, nameof(IDocumentSeller.SellerCompanyId)))));
    }

    internal static IQueryable<CompanyBranch> UndeletedCompanyBranches(PaymentContext db)
    {
        var branches = db.CompanyBranches.AsNoTracking();
        return db.Model.FindEntityType(typeof(CompanyBranch))?.FindProperty("IsDeleted") is null ? branches
            : branches.Where(branch => !EF.Property<bool>(branch, "IsDeleted"));
    }

    public static IQueryable<Payment> ScopePaymentCompanies(PaymentContext db, IQueryable<Payment> query, int[] allowed)
    {
        var companies = db.LegalEntities.AsNoTracking().Where(company => allowed.Contains(company.Id));
        if (db.Model.FindEntityType(typeof(LegalEntity))?.FindProperty("IsDeleted") is not null)
            companies = companies.Where(company => !EF.Property<bool>(company, "IsDeleted"));
        var branches = db.CompanyBranches.AsNoTracking();
        if (db.Model.FindEntityType(typeof(CompanyBranch))?.FindProperty("IsDeleted") is not null)
            branches = branches.Where(branch => !EF.Property<bool>(branch, "IsDeleted"));
        return query.Where(payment =>
            (!payment.SellerCompanyId.HasValue || companies.Any(company => company.Id == payment.SellerCompanyId))
            && (!payment.SellerBranchId.HasValue || branches.Any(branch => branch.Id == payment.SellerBranchId
                && companies.Any(company => company.Id == branch.LegalEntityId)
                && (!payment.SellerCompanyId.HasValue || payment.SellerCompanyId == branch.LegalEntityId))));
    }

    public static async Task<BankStatementImportAccessContext?> ResolveAsync(
        PaymentContext db, ClaimsPrincipal? user, CancellationToken ct)
    {
        var section = await db.ResolveSectionAccessAsync(user, RolePermissionCatalog.Payments, ct, requireApprovedUser: true);
        if (section is null) return null;
        var own = (await OwnCompanyRegistry.LoadIdsAsync(db, ct)).ToArray();
        var allowed = section.Visibility.IsAdmin ? own : await db.UserCompanyAccesses.AsNoTracking()
            .Where(item => item.UserId == section.Visibility.UserId && own.Contains(item.LegalEntityId)
                && item.User.IsEmployee && item.User.IsActive && item.User.IsApproved)
            .Select(item => item.LegalEntityId).ToArrayAsync(ct);
        return new(section, own, allowed);
    }

    public static IQueryable<MoneyAccount> ScopeAccounts(
        IQueryable<MoneyAccount> query, BankStatementImportAccessContext access, bool includeUnknownForAdmin = false,
        bool ignoreQueryFilters = true)
    {
        var allowed = access.AllowedCompanyIds;
        var allowUnknown = includeUnknownForAdmin && access.Visibility.IsAdmin;
        if (ignoreQueryFilters) query = query.IgnoreQueryFilters();
        return query.Where(account =>
            account.CompanyId.HasValue && allowed.Contains(account.CompanyId.Value)
            || allowUnknown && !account.CompanyId.HasValue);
    }

    public static IQueryable<MoneyAccount> CoherentAccounts(PaymentContext db, IQueryable<MoneyAccount> query, bool forNewOperations = false)
    {
        query = query.Where(account => account.CompanyId.HasValue && account.Company != null
            && (!account.BankConnectionAccountId.HasValue
                || account.Source == MoneyOperationSource.Bank && account.BankConnectionAccount!.BankConnection.LegalEntityId == account.CompanyId
                    && account.BankConnectionAccount.AccountNumber == account.BankAccountNumber
                    && (account.BankConnectionAccount.Bik ?? "") == (account.BankBik ?? "")
                    && account.BankConnectionAccount.Currency == account.Currency));
        if (db.Model.FindEntityType(typeof(LegalEntity))?.FindProperty("IsDeleted") is not null)
            query = query.Where(account => !EF.Property<bool>(account.Company!, "IsDeleted"));
        if (forNewOperations)
            query = ActiveAccountState(query).Where(account => !account.BankConnectionAccountId.HasValue
                || !account.BankConnectionAccount!.IsHidden && account.BankConnectionAccount.Status == BankAccountStatuses.Open);
        return query;
    }

    // Apply after company and bank-owner coherence has been established. This is
    // also the existing metadata-update rule when the bank is closed or hidden.
    public static IQueryable<MoneyAccount> ActiveAccountState(IQueryable<MoneyAccount> query)
        => query.Where(account => account.IsActive && account.Company != null
            && account.Company.ProfileStatus == CompanyProfileStatuses.Active);

    public static IQueryable<BankStatementImportBatch> ScopeBatches(
        PaymentContext db, IQueryable<BankStatementImportBatch> query,
        BankStatementImportAccessContext access, bool forMutation = false)
    {
        // Administrators retain read-only access to unattributed historical data.
        if (access.Visibility.IsAdmin && !forMutation) return query.IgnoreQueryFilters();
        var local = ScopeLocalBatches(db, db.BankStatementImportBatches, access);
        var scoped = ScopeLocalBatches(db, query, access)
            .Where(batch => !batch.ParentBatchId.HasValue || local.Any(parent =>
                parent.Id == batch.ParentBatchId.Value
                && parent.CompanyId == batch.CompanyId && parent.MoneyAccountId == batch.MoneyAccountId))
            .Where(batch => !batch.Rows.Any(row => row.OriginRowId.HasValue
                && !local.Any(origin => origin.Id == row.OriginRow!.BatchId
                    && origin.CompanyId == batch.CompanyId && origin.MoneyAccountId == batch.MoneyAccountId)));
        return forMutation ? scoped.Where(batch =>
            batch.Source == BankStatementImportSources.File
            || batch.Source == BankStatementImportSources.ManualBank
            || batch.Source == BankStatementImportSources.BackgroundBank) : scoped;
    }

    private static IQueryable<BankStatementImportBatch> ScopeLocalBatches(
        PaymentContext db, IQueryable<BankStatementImportBatch> query, BankStatementImportAccessContext access)
    {
        var allowedCompanies = access.AllowedCompanyIds;
        var seesAll = access.Visibility.SeesAll;
        var visible = access.Visibility.VisibleUserIds.ToArray();
        var money = db.MoneyOperations.IgnoreQueryFilters();
        var visibleMoney = money.Where(operation => operation.Source == MoneyOperationSource.Bank
            && (seesAll || operation.ResponsibleUserId.HasValue && visible.Contains(operation.ResponsibleUserId.Value)));
        var accounts = db.MoneyAccounts.IgnoreQueryFilters();
        // Separate indexed EXISTS branches cover the batch FK, row FK and durable
        // movement key, including archived identities whose row FK was cleared.
        return query.IgnoreQueryFilters().Where(batch =>
            batch.CompanyId.HasValue && allowedCompanies.Contains(batch.CompanyId.Value)
            && batch.MoneyAccountId.HasValue
            && accounts.Any(account => account.Id == batch.MoneyAccountId.Value
                && account.CompanyId == batch.CompanyId && account.Source == MoneyOperationSource.Bank
                && (!account.BankConnectionAccountId.HasValue || db.BankConnectionAccounts.Any(linked =>
                    linked.Id == account.BankConnectionAccountId.Value
                    && linked.BankConnection.LegalEntityId == batch.CompanyId
                    && linked.AccountNumber == account.BankAccountNumber
                    && (linked.Bik ?? "") == (account.BankBik ?? "") && linked.Currency == account.Currency
                    && (!batch.BankConnectionAccountId.HasValue || linked.Id == batch.BankConnectionAccountId.Value)))
                && (!batch.BankConnectionAccountId.HasValue || db.BankConnectionAccounts.Any(bank =>
                    bank.Id == batch.BankConnectionAccountId.Value
                    && bank.BankConnection.LegalEntityId == batch.CompanyId
                    && bank.AccountNumber == account.BankAccountNumber
                    && (bank.Bik ?? "") == (account.BankBik ?? "") && bank.Currency == account.Currency)))
            && (seesAll || batch.ResponsibleUserId.HasValue && visible.Contains(batch.ResponsibleUserId.Value))
            && !money.Any(operation => operation.BankStatementImportBatchId == batch.Id
                && (operation.Source != MoneyOperationSource.Bank || operation.MoneyAccountId != batch.MoneyAccountId
                    || !seesAll && (!operation.ResponsibleUserId.HasValue || !visible.Contains(operation.ResponsibleUserId.Value))))
            && !batch.Rows.Any(row => row.MoneyOperationId.HasValue
                && !visibleMoney.Any(permitted => permitted.Id == row.MoneyOperationId.Value && permitted.MoneyAccountId == batch.MoneyAccountId))
            && !batch.Rows.Any(row => row.MovementKey != null && money.Any(operation => operation.SourceMovementKey == row.MovementKey
                && (operation.Source != MoneyOperationSource.Bank || operation.MoneyAccountId != batch.MoneyAccountId
                    || !seesAll && (!operation.ResponsibleUserId.HasValue || !visible.Contains(operation.ResponsibleUserId.Value)))))
            && !batch.Rows.Any(row => row.BankStatementOperationId.HasValue
                && !accounts.Any(account => account.Id == batch.MoneyAccountId.Value
                    && row.BankStatementOperation!.BankConnectionAccount.BankConnection.LegalEntityId == batch.CompanyId
                    && row.BankStatementOperation.BankConnectionAccount.AccountNumber == account.BankAccountNumber
                    && (row.BankStatementOperation.BankConnectionAccount.Bik ?? "") == (account.BankBik ?? "")
                    && row.BankStatementOperation.BankConnectionAccount.Currency == account.Currency)));
    }
}
